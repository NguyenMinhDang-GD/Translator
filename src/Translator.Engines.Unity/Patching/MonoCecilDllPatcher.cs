using Mono.Cecil;
using Mono.Cecil.Cil;
using Translator.Core.Models;

namespace Translator.Engines.Unity.Patching;

public static class MonoCecilDllPatcher
{
    public static async Task<int> PatchFileAtomicAsync(
        string backupDllPath,
        string targetGameDllPath,
        IReadOnlyList<PatchPayload> payloads,
        CancellationToken cancellationToken = default)
    {
        var keyToTranslation = new Dictionary<string, string>(StringComparer.Ordinal);
        var instructionCoordinates = new Dictionary<(string TypeFullName, string MethodSig, int InstrIdx), string>();

        foreach (PatchPayload payload in payloads)
        {
            foreach (TextEntry entry in payload.Package.Entries)
            {
                if (string.IsNullOrEmpty(entry.TranslatedText))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.Key))
                {
                    keyToTranslation[entry.Key] = entry.TranslatedText;
                }

                if (!string.IsNullOrWhiteSpace(entry.Location.FieldOrInnerKey))
                {
                    keyToTranslation[entry.Location.FieldOrInnerKey] = entry.TranslatedText;
                }

                if (!string.IsNullOrWhiteSpace(entry.Location.CodeTypeFullName) &&
                    !string.IsNullOrWhiteSpace(entry.Location.CodeMethodName) &&
                    entry.Location.CodeInstructionIndex is >= 0)
                {
                    instructionCoordinates[(
                        entry.Location.CodeTypeFullName,
                        entry.Location.CodeMethodName,
                        entry.Location.CodeInstructionIndex.Value)] = entry.TranslatedText;
                }
            }
        }

        if (keyToTranslation.Count == 0 && instructionCoordinates.Count == 0)
        {
            return 0;
        }

        byte[] originalDllBytes = await File.ReadAllBytesAsync(backupDllPath, cancellationToken).ConfigureAwait(false);
        using var inputStream = new MemoryStream(originalDllBytes);

        using var resolver = new DefaultAssemblyResolver();
        string? managedDir = Path.GetDirectoryName(targetGameDllPath);
        if (!string.IsNullOrEmpty(managedDir) && Directory.Exists(managedDir))
        {
            resolver.AddSearchDirectory(managedDir);
        }

        string? backupDir = Path.GetDirectoryName(backupDllPath);
        if (!string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir))
        {
            resolver.AddSearchDirectory(backupDir);
        }

        using var assembly = AssemblyDefinition.ReadAssembly(
            inputStream,
            new ReaderParameters
            {
                ReadWrite = false,
                InMemory = true,
                AssemblyResolver = resolver
            });

        int patchedCount = 0;
        foreach (ModuleDefinition module in assembly.Modules)
        {
            foreach (TypeDefinition type in module.Types)
            {
                cancellationToken.ThrowIfCancellationRequested();
                patchedCount += PatchTypeRecursive(type, keyToTranslation, instructionCoordinates, cancellationToken);
            }
        }

        if (patchedCount == 0)
        {
            return 0;
        }

        string tempDllPath = $"{targetGameDllPath}.translator.tmp";
        await using (var outFs = new FileStream(tempDllPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        {
            assembly.Write(outFs);
            await outFs.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempDllPath, targetGameDllPath, overwrite: true);
        return patchedCount;
    }

    private static int PatchTypeRecursive(
        TypeDefinition type,
        Dictionary<string, string> keyToTranslation,
        Dictionary<(string TypeFullName, string MethodSig, int InstrIdx), string> instructionCoordinates,
        CancellationToken cancellationToken)
    {
        if (type.FullName == "<Module>")
        {
            return 0;
        }

        int patched = 0;

        // 1. Constant string fields
        if (type.HasFields)
        {
            foreach (FieldDefinition field in type.Fields)
            {
                if (field.HasConstant && field.Constant is string)
                {
                    string constKey = $"const::{type.FullName}::{field.Name}";
                    if (keyToTranslation.TryGetValue(constKey, out string? translated) &&
                        !string.IsNullOrEmpty(translated))
                    {
                        field.Constant = translated;
                        patched++;
                    }
                }
            }
        }

        // 2. Method IL OpCodes.Ldstr instructions
        if (type.HasMethods)
        {
            foreach (MethodDefinition method in type.Methods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!method.HasBody)
                {
                    continue;
                }

                string methodSig = BuildCompactMethodSignature(method);
                var instructions = method.Body.Instructions;
                int ldstrOrdinal = 0;

                for (int i = 0; i < instructions.Count; i++)
                {
                    Instruction instr = instructions[i];
                    if (instr.OpCode != OpCodes.Ldstr || instr.Operand is not string)
                    {
                        continue;
                    }

                    int currentOrdinal = ldstrOrdinal++;
                    string ordinalKey = $"il::{type.FullName}::{methodSig}::ldstr[{currentOrdinal}]";

                    if (keyToTranslation.TryGetValue(ordinalKey, out string? translatedByKey) &&
                        !string.IsNullOrEmpty(translatedByKey))
                    {
                        instr.Operand = translatedByKey;
                        patched++;
                    }
                    else if (instructionCoordinates.TryGetValue((type.FullName, methodSig, i), out string? translatedByCoord) &&
                             !string.IsNullOrEmpty(translatedByCoord))
                    {
                        instr.Operand = translatedByCoord;
                        patched++;
                    }
                }
            }
        }

        // 3. Nested types (coroutines & async state machines)
        if (type.HasNestedTypes)
        {
            foreach (TypeDefinition nested in type.NestedTypes)
            {
                patched += PatchTypeRecursive(nested, keyToTranslation, instructionCoordinates, cancellationToken);
            }
        }

        return patched;
    }

    private static string BuildCompactMethodSignature(MethodDefinition method)
    {
        if (!method.HasParameters)
        {
            return $"{method.Name}()";
        }

        string paramList = string.Join(",", method.Parameters.Select(p => p.ParameterType.Name));
        return $"{method.Name}({paramList})";
    }
}
