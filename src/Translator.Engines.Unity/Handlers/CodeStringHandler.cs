using System.Buffers.Binary;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Translator.Core.Exporting;
using Translator.Core.Filters;
using Translator.Core.Models;
using Translator.Engines.Unity.Detection;

namespace Translator.Engines.Unity.Handlers;

public static class CodeStringHandler
{
    public const uint Il2CppMagic = 0xFAB11BAFu;

    public static ScannedTarget CreateScannedTarget(ClassifiedUnityFile file)
    {
        string fileName = Path.GetFileName(file.FullPath);
        bool isPrimaryCode = fileName.Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase) ||
                             fileName.Equals("global-metadata.dat", StringComparison.OrdinalIgnoreCase);

        return new ScannedTarget
        {
            TargetId = $"Code::{file.RelativePath}",
            DisplayName = fileName,
            SourceKind = TextSourceKind.CodeString,
            ContainerRelativePath = file.RelativePath,
            ScriptClassName = file.Kind == ClassifiedUnityFileKind.Il2CppGlobalMetadata ? "IL2CPP Metadata" : "Managed IL",
            EstimatedSizeBytes = file.SizeBytes,
            RelevanceScore = isPrimaryCode ? 65 : 45,
            IsLikelyNoise = false,
            SupportsRawExport = false
        };
    }

    public static async Task<ExtractedTargetContent> ExtractAsync(
        GameProfile profile,
        ScannedTarget target,
        ClassifiedUnityFileKind kind,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.Combine(profile.RootPath, target.ContainerRelativePath);
        string sha256 = HashUtility.ComputeFileSha256(fullPath);

        IReadOnlyList<TextEntry> entries = kind == ClassifiedUnityFileKind.Il2CppGlobalMetadata
            ? await ExtractIl2CppGlobalMetadataFileAsync(fullPath, target.ContainerRelativePath, cancellationToken).ConfigureAwait(false)
            : ExtractManagedDllStrings(fullPath, target.ContainerRelativePath, cancellationToken);

        return new ExtractedTargetContent(
            target,
            RawText: null,
            RawBytes: null,
            RawExtension: null,
            ContainerSha256: sha256,
            InnerFormat: InnerTextFormat.StructuredFields,
            Entries: entries);
    }

    public static IReadOnlyList<TextEntry> ExtractManagedDllStrings(
        string dllFullPath,
        string containerRelativePath,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<TextEntry>();
        using var assembly = AssemblyDefinition.ReadAssembly(
            dllFullPath,
            new ReaderParameters { ReadWrite = false, InMemory = true });

        int entryCounter = 1;
        foreach (ModuleDefinition module in assembly.Modules)
        {
            foreach (TypeDefinition type in module.Types)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WalkTypeDefinition(type, containerRelativePath, entries, ref entryCounter, cancellationToken);
            }
        }

        return entries;
    }

    private static void WalkTypeDefinition(
        TypeDefinition type,
        string containerRelativePath,
        List<TextEntry> entries,
        ref int entryCounter,
        CancellationToken cancellationToken)
    {
        if (type.FullName == "<Module>")
        {
            return;
        }

        // 1. Constant string fields (e.g. public const string Title = "Start Game";)
        if (type.HasFields)
        {
            foreach (FieldDefinition field in type.Fields)
            {
                if (field.HasConstant && field.Constant is string constStr && !string.IsNullOrWhiteSpace(constStr))
                {
                    (int score, bool isNoise) = TextHeuristicScorer.EvaluateString(constStr, field.Name);
                    string key = $"const::{type.FullName}::{field.Name}";

                    entries.Add(new TextEntry
                    {
                        Id = $"entry_{entryCounter++:D5}",
                        Key = key,
                        Context = $"{type.FullName}.{field.Name} (const)",
                        OriginalText = constStr,
                        RelevanceScore = score,
                        IsSystemNoise = isNoise,
                        Location = new EntryLocation
                        {
                            ContainerRelativePath = containerRelativePath,
                            FieldOrInnerKey = key,
                            CodeTypeFullName = type.FullName,
                            CodeMethodName = $"const:{field.Name}"
                        }
                    });
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

                var instructions = method.Body.Instructions;
                int ldstrOrdinal = 0;

                for (int i = 0; i < instructions.Count; i++)
                {
                    Instruction instr = instructions[i];
                    if (instr.OpCode != OpCodes.Ldstr || instr.Operand is not string strVal)
                    {
                        continue;
                    }

                    int currentOrdinal = ldstrOrdinal++;
                    if (string.IsNullOrWhiteSpace(strVal))
                    {
                        continue;
                    }

                    (int score, bool isNoise) = TextHeuristicScorer.EvaluateString(strVal);
                    string methodSig = BuildCompactMethodSignature(method);
                    string key = $"il::{type.FullName}::{methodSig}::ldstr[{currentOrdinal}]";

                    entries.Add(new TextEntry
                    {
                        Id = $"entry_{entryCounter++:D5}",
                        Key = key,
                        Context = $"{type.FullName}.{method.Name} (IL_{instr.Offset:X4})",
                        OriginalText = strVal,
                        RelevanceScore = score,
                        IsSystemNoise = isNoise,
                        Location = new EntryLocation
                        {
                            ContainerRelativePath = containerRelativePath,
                            FieldOrInnerKey = key,
                            CodeTypeFullName = type.FullName,
                            CodeMethodName = methodSig,
                            CodeInstructionIndex = i
                        }
                    });
                }
            }
        }

        // 3. Recurse into NestedTypes (catches C# coroutine <DialogueRoutine>d__5 and async state machines!)
        if (type.HasNestedTypes)
        {
            foreach (TypeDefinition nested in type.NestedTypes)
            {
                WalkTypeDefinition(nested, containerRelativePath, entries, ref entryCounter, cancellationToken);
            }
        }
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

    public static async Task<IReadOnlyList<TextEntry>> ExtractIl2CppGlobalMetadataFileAsync(
        string metadataFullPath,
        string containerRelativePath,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(metadataFullPath, cancellationToken).ConfigureAwait(false);
        return ExtractIl2CppGlobalMetadataFromBytes(bytes, containerRelativePath, cancellationToken);
    }

    public static IReadOnlyList<TextEntry> ExtractIl2CppGlobalMetadataFromBytes(
        ReadOnlySpan<byte> data,
        string containerRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (data.Length < 24)
        {
            return [];
        }

        uint sanity = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
        int version = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4));
        if (sanity != Il2CppMagic)
        {
            throw new InvalidDataException($"global-metadata.dat có magic header 0x{sanity:X8} không hợp lệ (nghi bị mã hóa).");
        }

        int stringLiteralOffset = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8, 4));
        int stringLiteralByteSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(12, 4));
        int stringLiteralDataOffset = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(16, 4));
        int stringLiteralDataByteSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(20, 4));

        if (stringLiteralOffset < 0 ||
            stringLiteralByteSize <= 0 ||
            stringLiteralDataOffset < 0 ||
            stringLiteralDataByteSize <= 0 ||
            (long)stringLiteralOffset + stringLiteralByteSize > data.Length ||
            (long)stringLiteralDataOffset + stringLiteralDataByteSize > data.Length)
        {
            throw new InvalidDataException("Bảng StringLiteral trong global-metadata.dat có offset nằm ngoài phạm vi file.");
        }

        int literalCount = stringLiteralByteSize / 8;
        ReadOnlySpan<byte> literalTable = data.Slice(stringLiteralOffset, literalCount * 8);
        ReadOnlySpan<byte> literalDataPool = data.Slice(stringLiteralDataOffset, stringLiteralDataByteSize);

        var entries = new List<TextEntry>(Math.Min(literalCount, 32768));
        int entryCounter = 1;

        for (int literalIdx = 0; literalIdx < literalCount; literalIdx++)
        {
            if ((literalIdx & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<byte> row = literalTable.Slice(literalIdx * 8, 8);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(row[..4]);
            int dataIndex = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(4, 4));

            if (length == 0 || dataIndex < 0 || (long)dataIndex + length > literalDataPool.Length || length > 262144)
            {
                continue;
            }

            ReadOnlySpan<byte> strBytes = literalDataPool.Slice(dataIndex, (int)length);
            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(strBytes);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(decoded))
            {
                continue;
            }

            (int score, bool isNoise) = TextHeuristicScorer.EvaluateString(decoded);
            string key = $"il2cpp:literal[{literalIdx}]";

            entries.Add(new TextEntry
            {
                Id = $"entry_{entryCounter++:D6}",
                Key = key,
                Context = $"IL2CPP Metadata v{version} | Literal #{literalIdx} (+0x{dataIndex:X})",
                OriginalText = decoded,
                RelevanceScore = score,
                IsSystemNoise = isNoise,
                Location = new EntryLocation
                {
                    ContainerRelativePath = containerRelativePath,
                    FieldOrInnerKey = key,
                    MetadataLiteralIndex = literalIdx
                }
            });
        }

        return entries;
    }
}
