using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Translator.Core.Abstractions;
using Translator.Core.Filters;
using Translator.Core.Indexing;
using Translator.Core.Models;

namespace Translator.Core.Exporting;

public static class HashUtility
{
    public static string ComputeFileSha256(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return string.Empty;
        }

        using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 65536);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}

public sealed class ExtractionService
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<ExportSummary> ExportTargetsAsync(
        IGameEngineAdapter adapter,
        GameProfile profile,
        TwoPhaseScanCoordinator coordinator,
        IReadOnlyList<ScannedTarget> selectedTargets,
        IReadOnlyList<ScannedTarget> allKnownTargets,
        ExportOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.OutputDirectory);

        // Pre-detect duplicate TextAsset names within the same container across the whole game
        var nameCountsPerContainer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ScannedTarget t in allKnownTargets)
        {
            if (t.SourceKind == TextSourceKind.TextAsset)
            {
                string key = $"{t.ContainerRelativePath}|{t.BundleMemberName}|{SanitizeFileName(StripExtensionIfKnown(t.DisplayName))}";
                nameCountsPerContainer.TryGetValue(key, out int count);
                nameCountsPerContainer[key] = count + 1;
            }
        }

        var usedRelativeBasePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generatedFiles = new List<string>();
        var manifestRecords = new List<ExportedFileRecord>();
        var errors = new List<string>();

        int exportedTargets = 0;
        int exportedEntries = 0;
        int rawFilesWritten = 0;
        int total = selectedTargets.Count;
        int current = 0;

        foreach (ScannedTarget target in selectedTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current++;
            progress?.Report(new ScanProgress("Export", current, total, target.DisplayName));

            try
            {
                ExtractedTargetContent extracted = await coordinator
                    .GetOrExtractTargetAsync(adapter, profile, target, cancellationToken)
                    .ConfigureAwait(false);

                List<TextEntry> entriesToExport = extracted.Entries
                    .Where(e => (options.IncludeSystemNoiseEntries || !e.IsSystemNoise) &&
                                LanguageDetector.MatchesFilter(e.Language, options.LanguageFilter))
                    .ToList();

                // When LanguageFilter is All and user explicitly selected a noise-only target, fall back to all entries;
                // When a specific language (e.g. English) is chosen and this file has 0 matching sentences, skip creating an empty file!
                if (entriesToExport.Count == 0)
                {
                    if (options.LanguageFilter == SourceLanguageFilter.All && extracted.Entries.Count > 0)
                    {
                        entriesToExport = extracted.Entries.ToList();
                    }
                    else if (!options.IncludeRawFile || !target.SupportsRawExport)
                    {
                        continue;
                    }
                }

                string relativeBase = BuildRelativeOutputBasePath(target, nameCountsPerContainer, usedRelativeBasePaths);
                string absoluteBase = Path.Combine(options.OutputDirectory, relativeBase);
                string? parentDir = Path.GetDirectoryName(absoluteBase);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                var package = new TranslationPackage
                {
                    Engine = profile.EngineName,
                    EngineVersion = profile.EngineVersion,
                    ScriptingBackend = profile.ScriptingBackend,
                    TargetId = target.TargetId,
                    DisplayName = target.DisplayName,
                    SourceKind = target.SourceKind,
                    ContainerRelativePath = target.ContainerRelativePath,
                    BundleMemberName = target.BundleMemberName,
                    OriginalContainerSha256 = extracted.ContainerSha256,
                    InnerFormat = extracted.InnerFormat,
                    Entries = entriesToExport
                };

                string? jsonRelPath = null;
                string? csvRelPath = null;
                string? rawRelPath = null;

                if (options.ExportTranslatorJson)
                {
                    string jsonPath = $"{absoluteBase}.translator.json";
                    string jsonText = JsonSerializer.Serialize(package, JsonWriteOptions);
                    await File.WriteAllTextAsync(jsonPath, jsonText, new UTF8Encoding(false), cancellationToken)
                        .ConfigureAwait(false);

                    jsonRelPath = Path.GetRelativePath(options.OutputDirectory, jsonPath);
                    generatedFiles.Add(jsonPath);
                }

                if (options.ExportCsv)
                {
                    string csvPath = $"{absoluteBase}.csv";
                    await WriteCsvAsync(csvPath, entriesToExport, cancellationToken).ConfigureAwait(false);
                    csvRelPath = Path.GetRelativePath(options.OutputDirectory, csvPath);
                    generatedFiles.Add(csvPath);

                    // Ensure location metadata is preserved even if user unchecked .translator.json
                    if (!options.ExportTranslatorJson)
                    {
                        string companionManifestPath = $"{absoluteBase}.manifest.json";
                        string manifestText = JsonSerializer.Serialize(package, JsonWriteOptions);
                        await File.WriteAllTextAsync(companionManifestPath, manifestText, new UTF8Encoding(false), cancellationToken)
                            .ConfigureAwait(false);
                        generatedFiles.Add(companionManifestPath);
                    }
                }

                if (options.IncludeRawFile && target.SupportsRawExport)
                {
                    string rawExt = DetermineRawExtension(target, extracted);
                    string rawPath = $"{absoluteBase}.raw{rawExt}";

                    if (extracted.RawBytes is { Length: > 0 })
                    {
                        await File.WriteAllBytesAsync(rawPath, extracted.RawBytes, cancellationToken)
                            .ConfigureAwait(false);
                        rawFilesWritten++;
                        rawRelPath = Path.GetRelativePath(options.OutputDirectory, rawPath);
                        generatedFiles.Add(rawPath);
                    }
                    else if (!string.IsNullOrEmpty(extracted.RawText))
                    {
                        await File.WriteAllTextAsync(rawPath, extracted.RawText, new UTF8Encoding(false), cancellationToken)
                            .ConfigureAwait(false);
                        rawFilesWritten++;
                        rawRelPath = Path.GetRelativePath(options.OutputDirectory, rawPath);
                        generatedFiles.Add(rawPath);
                    }
                }

                manifestRecords.Add(new ExportedFileRecord(
                    target.TargetId,
                    target.DisplayName,
                    target.SourceKind,
                    target.ContainerRelativePath,
                    jsonRelPath,
                    csvRelPath,
                    rawRelPath,
                    entriesToExport.Count));

                exportedTargets++;
                exportedEntries += entriesToExport.Count;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{target.DisplayName} ({target.ContainerRelativePath}): {ex.Message}");
            }
        }

        var projectManifest = new ProjectExtractionManifest
        {
            GameName = profile.GameName,
            GameRootPath = profile.RootPath,
            Engine = profile.EngineName,
            EngineVersion = profile.EngineVersion,
            ScriptingBackend = profile.ScriptingBackend,
            Files = manifestRecords
        };

        string projectManifestPath = Path.Combine(options.OutputDirectory, "manifest.project.json");
        string projectManifestJson = JsonSerializer.Serialize(projectManifest, JsonWriteOptions);
        await File.WriteAllTextAsync(projectManifestPath, projectManifestJson, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        generatedFiles.Add(projectManifestPath);

        return new ExportSummary(
            exportedTargets,
            exportedEntries,
            rawFilesWritten,
            options.OutputDirectory,
            generatedFiles,
            errors);
    }

    private static string BuildRelativeOutputBasePath(
        ScannedTarget target,
        Dictionary<string, int> nameCountsPerContainer,
        HashSet<string> usedRelativeBasePaths)
    {
        string normalizedContainer = target.ContainerRelativePath.Replace('\\', '/');
        string safeContainerFolder = SanitizeRelativeDirectory(normalizedContainer);

        string basePath;
        switch (target.SourceKind)
        {
            case TextSourceKind.LooseFile:
            {
                string? dir = Path.GetDirectoryName(normalizedContainer);
                string safeDir = string.IsNullOrEmpty(dir) ? string.Empty : SanitizeRelativeDirectory(dir);
                string fileNoExt = SanitizeFileName(Path.GetFileNameWithoutExtension(normalizedContainer));
                basePath = string.IsNullOrEmpty(safeDir)
                    ? Path.Combine("LooseFiles", fileNoExt)
                    : Path.Combine("LooseFiles", safeDir, fileNoExt);
                break;
            }

            case TextSourceKind.TextAsset:
            {
                string rootBucket = target.BundleMemberName is not null ? "Bundles" : "Assets";
                string cleanName = SanitizeFileName(StripExtensionIfKnown(target.DisplayName));
                string dupKey = $"{target.ContainerRelativePath}|{target.BundleMemberName}|{cleanName}";
                if (nameCountsPerContainer.TryGetValue(dupKey, out int dupCount) && dupCount > 1 && target.PrimaryPathId.HasValue)
                {
                    cleanName = $"{cleanName}_{FormatPathId(target.PrimaryPathId.Value)}";
                }

                basePath = Path.Combine(rootBucket, safeContainerFolder, "TextAssets", cleanName);
                break;
            }

            case TextSourceKind.MonoBehaviour:
            {
                string rootBucket = target.BundleMemberName is not null ? "Bundles" : "Assets";
                string className = SanitizeFileName(string.IsNullOrWhiteSpace(target.ScriptClassName)
                    ? target.DisplayName
                    : $"{target.ScriptClassName}_Group");
                basePath = Path.Combine(rootBucket, safeContainerFolder, "MonoBehaviours", className);
                break;
            }

            case TextSourceKind.CodeString:
            default:
            {
                string fileName = SanitizeFileName(Path.GetFileName(normalizedContainer));
                basePath = Path.Combine("CodeStrings", fileName);
                break;
            }
        }

        string candidate = basePath;
        if (!usedRelativeBasePaths.Add(candidate))
        {
            string suffix = target.PrimaryPathId.HasValue
                ? FormatPathId(target.PrimaryPathId.Value)
                : Guid.NewGuid().ToString("N")[..6];
            candidate = $"{basePath}_{suffix}";
            int counter = 2;
            while (!usedRelativeBasePaths.Add(candidate))
            {
                candidate = $"{basePath}_{suffix}_{counter++}";
            }
        }

        return candidate;
    }

    private static async Task WriteCsvAsync(
        string csvPath,
        IReadOnlyList<TextEntry> entries,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(entries.Count * 96 + 64);
        sb.AppendLine("Id,Key,Context,OriginalText,TranslatedText");

        foreach (TextEntry entry in entries)
        {
            sb.Append(EscapeCsv(entry.Id)).Append(',');
            sb.Append(EscapeCsv(entry.Key)).Append(',');
            sb.Append(EscapeCsv(entry.Context)).Append(',');
            sb.Append(EscapeCsv(entry.OriginalText)).Append(',');
            sb.AppendLine(EscapeCsv(entry.TranslatedText));
        }

        // Write UTF-8 with BOM so Microsoft Excel on Windows displays Vietnamese characters properly
        await File.WriteAllTextAsync(csvPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }

    private static string DetermineRawExtension(ScannedTarget target, ExtractedTargetContent extracted)
    {
        if (!string.IsNullOrWhiteSpace(extracted.RawExtension))
        {
            return extracted.RawExtension.StartsWith('.') ? extracted.RawExtension : $".{extracted.RawExtension}";
        }

        return extracted.InnerFormat switch
        {
            InnerTextFormat.Json => ".json",
            InnerTextFormat.Csv => ".csv",
            InnerTextFormat.Tsv => ".tsv",
            InnerTextFormat.Xml => ".xml",
            InnerTextFormat.IniKeyValue => ".ini",
            _ => ".txt"
        };
    }

    private static string StripExtensionIfKnown(string name)
    {
        string ext = Path.GetExtension(name);
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tsv", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bytes", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(name);
        }

        return name;
    }

    private static string FormatPathId(long pathId) =>
        pathId < 0 ? $"n{Math.Abs(pathId)}" : pathId.ToString();

    private static string SanitizeRelativeDirectory(string relativePath)
    {
        string[] parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = SanitizeFileName(parts[i]);
        }

        return Path.Combine(parts);
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unnamed";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string cleaned = sb.ToString().TrimEnd('.', ' ');
        return string.IsNullOrEmpty(cleaned) ? "Unnamed" : cleaned;
    }
}
