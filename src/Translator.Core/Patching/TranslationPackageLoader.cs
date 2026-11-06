using System.Text;
using System.Text.Json;
using Translator.Core.Abstractions;
using Translator.Core.Models;
using Translator.Core.Parsers;

namespace Translator.Core.Patching;

public sealed class TranslationPackageLoader : IPatchPackageLoader
{
    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<IReadOnlyList<PatchPayload>> LoadFromDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Không tìm thấy thư mục bản dịch: {directoryPath}");
        }

        var payloads = new List<PatchPayload>();
        var loadedTargetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Check for manifest.project.json at root
        string projectManifestPath = Path.Combine(directoryPath, "manifest.project.json");
        if (File.Exists(projectManifestPath))
        {
            string projJson = await File.ReadAllTextAsync(projectManifestPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            ProjectExtractionManifest? projectManifest = JsonSerializer.Deserialize<ProjectExtractionManifest>(projJson, JsonReadOptions);

            if (projectManifest?.Files is { Count: > 0 })
            {
                foreach (ExportedFileRecord record in projectManifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PatchPayload? payload = await TryLoadRecordFromProjectAsync(directoryPath, record, cancellationToken).ConfigureAwait(false);
                    if (payload is not null && payload.TranslatedEntryCount > 0)
                    {
                        payloads.Add(payload);
                        loadedTargetIds.Add(payload.Package.TargetId);
                    }
                }
            }
        }

        // 2. Also scan directory for any .translator.json, .manifest.json, or .csv files not yet loaded
        var processedBasePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("manifest.project.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("vault_manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? basePath = TryGetBasePathWithoutKnownSuffix(file);
            if (basePath is null || !processedBasePaths.Add(basePath))
            {
                continue;
            }

            PatchPayload? payload = await TryLoadFromBasePathAsync(basePath, cancellationToken).ConfigureAwait(false);
            if (payload is not null &&
                payload.TranslatedEntryCount > 0 &&
                loadedTargetIds.Add(payload.Package.TargetId))
            {
                payloads.Add(payload);
            }
        }

        return payloads;
    }

    public async Task<IReadOnlyList<PatchPayload>> LoadFromFileAsync(
        string filePath,
        string? optionalManifestPath = null,
        ScannedTarget? fallbackTarget = null,
        GameProfile? fallbackProfile = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Không tìm thấy file bản dịch: {filePath}", filePath);
        }

        // 1. .translator.json or .manifest.json
        if (filePath.EndsWith(".translator.json", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            TranslationPackage pkg = await ReadPackageJsonAsync(filePath, cancellationToken).ConfigureAwait(false);
            string basePath = filePath.EndsWith(".translator.json", StringComparison.OrdinalIgnoreCase)
                ? filePath[..^".translator.json".Length]
                : filePath[..^".manifest.json".Length];

            string siblingCsv = $"{basePath}.csv";
            if (File.Exists(siblingCsv))
            {
                pkg = await MergeCsvTranslationsIntoPackageAsync(pkg, siblingCsv, cancellationToken).ConfigureAwait(false);
            }

            return [new PatchPayload { SourceFilePath = filePath, Package = pkg }];
        }

        // 2. .csv file (paired with .manifest.json / .translator.json, or Master.csv, or fallbackTarget)
        if (filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
            !filePath.Contains(".raw.", StringComparison.OrdinalIgnoreCase))
        {
            string? companionPath = optionalManifestPath;
            if (string.IsNullOrWhiteSpace(companionPath) || !File.Exists(companionPath))
            {
                string baseNoCsv = filePath[..^".csv".Length];
                string candManifest = $"{baseNoCsv}.manifest.json";
                string candTranslator = $"{baseNoCsv}.translator.json";

                if (File.Exists(candManifest))
                {
                    companionPath = candManifest;
                }
                else if (File.Exists(candTranslator))
                {
                    companionPath = candTranslator;
                }
            }

            if (!string.IsNullOrWhiteSpace(companionPath) && File.Exists(companionPath))
            {
                TranslationPackage pkg = await ReadPackageJsonAsync(companionPath, cancellationToken).ConfigureAwait(false);
                pkg = await MergeCsvTranslationsIntoPackageAsync(pkg, filePath, cancellationToken).ConfigureAwait(false);
                return [new PatchPayload { SourceFilePath = filePath, Package = pkg }];
            }

            // Check if this is a Master.csv with TargetId column or matches a record in manifest.project.json
            IReadOnlyList<PatchPayload> masterPayloads = await TryLoadMasterOrProjectCsvAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (masterPayloads.Count > 0)
            {
                return masterPayloads;
            }

            // Fallback: use fallbackTarget if user selected a target in GUI
            if (fallbackTarget is not null && fallbackProfile is not null)
            {
                TranslationPackage fallbackPkg = await BuildPackageFromCsvAndTargetAsync(
                    filePath,
                    fallbackTarget,
                    fallbackProfile,
                    cancellationToken).ConfigureAwait(false);
                return [new PatchPayload { SourceFilePath = filePath, Package = fallbackPkg }];
            }

            throw new InvalidOperationException(
                $"Không tìm thấy file siêu dữ liệu (.manifest.json hoặc .translator.json) đi kèm với '{Path.GetFileName(filePath)}'. " +
                "Hãy đặt file .manifest.json cùng thư mục với file .csv hoặc chọn mục tương ứng trên danh sách bên trái.");
        }

        // 3. Raw file override (*.raw.* or standalone raw file with selected target)
        TranslationPackage? rawPkg = null;
        string? rawCompanion = optionalManifestPath ?? TryFindCompanionManifestForRawFile(filePath);
        if (!string.IsNullOrWhiteSpace(rawCompanion) && File.Exists(rawCompanion))
        {
            rawPkg = await ReadPackageJsonAsync(rawCompanion, cancellationToken).ConfigureAwait(false);
        }
        else if (fallbackTarget is not null && fallbackProfile is not null)
        {
            rawPkg = CreateEmptyPackageForTarget(fallbackTarget, fallbackProfile);
        }

        if (rawPkg is null)
        {
            throw new InvalidOperationException(
                $"Không xác định được tài nguyên đích cho file Raw '{Path.GetFileName(filePath)}'. " +
                "Hãy chọn mục đích trên danh sách trước khi nạp file Raw.");
        }

        byte[] rawBytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        string? rawText = null;
        if (!filePath.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
        {
            rawText = DecodeTextPreservingBom(rawBytes);
        }

        return
        [
            new PatchPayload
            {
                SourceFilePath = filePath,
                Package = rawPkg,
                RawTextOverride = rawText,
                RawBytesOverride = rawBytes
            }
        ];
    }

    private static async Task<PatchPayload?> TryLoadRecordFromProjectAsync(
        string rootDir,
        ExportedFileRecord record,
        CancellationToken cancellationToken)
    {
        string? jsonPath = !string.IsNullOrWhiteSpace(record.StructuredJsonRelativePath)
            ? Path.Combine(rootDir, record.StructuredJsonRelativePath)
            : null;

        string? csvPath = !string.IsNullOrWhiteSpace(record.CsvRelativePath)
            ? Path.Combine(rootDir, record.CsvRelativePath)
            : null;

        string? companionManifestPath = null;
        if (csvPath is not null && csvPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            companionManifestPath = $"{csvPath[..^4]}.manifest.json";
        }

        TranslationPackage? pkg = null;
        string? primarySourcePath = null;

        if (jsonPath is not null && File.Exists(jsonPath))
        {
            pkg = await ReadPackageJsonAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            primarySourcePath = jsonPath;
        }
        else if (companionManifestPath is not null && File.Exists(companionManifestPath))
        {
            pkg = await ReadPackageJsonAsync(companionManifestPath, cancellationToken).ConfigureAwait(false);
            primarySourcePath = companionManifestPath;
        }

        if (pkg is null)
        {
            return null;
        }

        if (csvPath is not null && File.Exists(csvPath))
        {
            pkg = await MergeCsvTranslationsIntoPackageAsync(pkg, csvPath, cancellationToken).ConfigureAwait(false);
            primarySourcePath = csvPath;
        }

        // If no entry was translated in CSV/JSON, check if a Raw file override exists and differs
        int translatedEntries = pkg.Entries.Count(e => !string.IsNullOrEmpty(e.TranslatedText));
        if (translatedEntries == 0 && !string.IsNullOrWhiteSpace(record.RawRelativePath))
        {
            string rawPath = Path.Combine(rootDir, record.RawRelativePath);
            if (File.Exists(rawPath))
            {
                byte[] rawBytes = await File.ReadAllBytesAsync(rawPath, cancellationToken).ConfigureAwait(false);
                string? rawText = rawPath.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : DecodeTextPreservingBom(rawBytes);

                return new PatchPayload
                {
                    SourceFilePath = rawPath,
                    Package = pkg,
                    RawTextOverride = rawText,
                    RawBytesOverride = rawBytes
                };
            }
        }

        return new PatchPayload
        {
            SourceFilePath = primarySourcePath!,
            Package = pkg
        };
    }

    private static async Task<PatchPayload?> TryLoadFromBasePathAsync(
        string basePath,
        CancellationToken cancellationToken)
    {
        string translatorJson = $"{basePath}.translator.json";
        string manifestJson = $"{basePath}.manifest.json";
        string csvPath = $"{basePath}.csv";

        TranslationPackage? pkg = null;
        string? sourcePath = null;

        if (File.Exists(translatorJson))
        {
            pkg = await ReadPackageJsonAsync(translatorJson, cancellationToken).ConfigureAwait(false);
            sourcePath = translatorJson;
        }
        else if (File.Exists(manifestJson))
        {
            pkg = await ReadPackageJsonAsync(manifestJson, cancellationToken).ConfigureAwait(false);
            sourcePath = manifestJson;
        }

        if (pkg is null)
        {
            return null;
        }

        if (File.Exists(csvPath))
        {
            pkg = await MergeCsvTranslationsIntoPackageAsync(pkg, csvPath, cancellationToken).ConfigureAwait(false);
            sourcePath = csvPath;
        }

        return new PatchPayload
        {
            SourceFilePath = sourcePath!,
            Package = pkg
        };
    }

    private static async Task<TranslationPackage> ReadPackageJsonAsync(
        string jsonPath,
        CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(jsonPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        TranslationPackage? pkg = JsonSerializer.Deserialize<TranslationPackage>(json, JsonReadOptions);
        if (pkg is null)
        {
            throw new InvalidDataException($"File gói dịch không hợp lệ: {jsonPath}");
        }

        return pkg;
    }

    private static async Task<TranslationPackage> MergeCsvTranslationsIntoPackageAsync(
        TranslationPackage package,
        string csvPath,
        CancellationToken cancellationToken)
    {
        string csvText = await File.ReadAllTextAsync(csvPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        List<List<string>> rows = CsvTsvContentParser.ParseTable(csvText, ',');
        if (rows.Count <= 1)
        {
            return package;
        }

        // Header is typically: Id,Key,Context,OriginalText,TranslatedText
        List<string> header = rows[0];
        int idCol = FindColumnIndex(header, "Id", fallback: 0);
        int keyCol = FindColumnIndex(header, "Key", fallback: 1);
        int transCol = FindColumnIndex(header, "TranslatedText", fallback: header.Count - 1);

        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int r = 1; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            if (transCol < 0 || transCol >= row.Count)
            {
                continue;
            }

            string translated = row[transCol];
            if (string.IsNullOrEmpty(translated) || IsExcelFormulaError(translated))
            {
                continue;
            }

            if (idCol >= 0 && idCol < row.Count && !string.IsNullOrWhiteSpace(row[idCol]))
            {
                byId[row[idCol].Trim()] = translated;
            }

            if (keyCol >= 0 && keyCol < row.Count && !string.IsNullOrWhiteSpace(row[keyCol]))
            {
                byKey[row[keyCol].Trim()] = translated;
            }
        }

        if (byId.Count == 0 && byKey.Count == 0)
        {
            return package;
        }

        for (int i = 0; i < package.Entries.Count; i++)
        {
            TextEntry entry = package.Entries[i];
            if (byId.TryGetValue(entry.Id, out string? transById) && !string.IsNullOrEmpty(transById))
            {
                package.Entries[i] = entry with { TranslatedText = transById };
            }
            else if (byKey.TryGetValue(entry.Key, out string? transByKey) && !string.IsNullOrEmpty(transByKey))
            {
                package.Entries[i] = entry with { TranslatedText = transByKey };
            }
            else if (byKey.TryGetValue(entry.Location.FieldOrInnerKey, out string? transByLoc) && !string.IsNullOrEmpty(transByLoc))
            {
                package.Entries[i] = entry with { TranslatedText = transByLoc };
            }
        }

        return package;
    }

    private static bool IsExcelFormulaError(string value)
    {
        ReadOnlySpan<char> trimmed = value.AsSpan().Trim();
        return trimmed.Equals("#NAME?", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("#VALUE!", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("#REF!", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("#DIV/0!", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("#N/A", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("#NUM!", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("#NULL!", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<PatchPayload>> TryLoadMasterOrProjectCsvAsync(
        string csvPath,
        CancellationToken cancellationToken)
    {
        string csvText = await File.ReadAllTextAsync(csvPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        List<List<string>> rows = CsvTsvContentParser.ParseTable(csvText, ',');
        if (rows.Count <= 1)
        {
            return [];
        }

        List<string> header = rows[0];
        int targetIdCol = FindColumnIndex(header, "TargetId", fallback: -1);
        if (targetIdCol < 0)
        {
            return [];
        }

        string? dir = Path.GetDirectoryName(csvPath);
        if (string.IsNullOrEmpty(dir))
        {
            return [];
        }

        // Load all manifests in directory to match by TargetId
        var packagesByTargetId = new Dictionary<string, TranslationPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            if (f.EndsWith(".translator.json", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    TranslationPackage p = await ReadPackageJsonAsync(f, cancellationToken).ConfigureAwait(false);
                    packagesByTargetId[p.TargetId] = p;
                }
                catch
                {
                }
            }
        }

        if (packagesByTargetId.Count == 0)
        {
            return [];
        }

        int idCol = FindColumnIndex(header, "Id", fallback: 1);
        int keyCol = FindColumnIndex(header, "Key", fallback: 2);
        int transCol = FindColumnIndex(header, "TranslatedText", fallback: header.Count - 1);

        var touchedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int r = 1; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            if (targetIdCol >= row.Count || transCol >= row.Count)
            {
                continue;
            }

            string targetId = row[targetIdCol].Trim();
            string translated = row[transCol];
            if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(translated) || IsExcelFormulaError(translated))
            {
                continue;
            }

            if (!packagesByTargetId.TryGetValue(targetId, out TranslationPackage? pkg))
            {
                continue;
            }

            string idVal = idCol >= 0 && idCol < row.Count ? row[idCol].Trim() : string.Empty;
            string keyVal = keyCol >= 0 && keyCol < row.Count ? row[keyCol].Trim() : string.Empty;

            for (int i = 0; i < pkg.Entries.Count; i++)
            {
                TextEntry entry = pkg.Entries[i];
                if ((!string.IsNullOrEmpty(idVal) && entry.Id.Equals(idVal, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(keyVal) && (entry.Key.Equals(keyVal, StringComparison.Ordinal) || entry.Location.FieldOrInnerKey.Equals(keyVal, StringComparison.Ordinal))))
                {
                    pkg.Entries[i] = entry with { TranslatedText = translated };
                    touchedTargets.Add(targetId);
                }
            }
        }

        return touchedTargets
            .Select(tid => new PatchPayload
            {
                SourceFilePath = csvPath,
                Package = packagesByTargetId[tid]
            })
            .ToList();
    }

    private static async Task<TranslationPackage> BuildPackageFromCsvAndTargetAsync(
        string csvPath,
        ScannedTarget target,
        GameProfile profile,
        CancellationToken cancellationToken)
    {
        string csvText = await File.ReadAllTextAsync(csvPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        List<List<string>> rows = CsvTsvContentParser.ParseTable(csvText, ',');

        List<string> header = rows.Count > 0 ? rows[0] : [];
        int idCol = FindColumnIndex(header, "Id", fallback: 0);
        int keyCol = FindColumnIndex(header, "Key", fallback: 1);
        int ctxCol = FindColumnIndex(header, "Context", fallback: 2);
        int origCol = FindColumnIndex(header, "OriginalText", fallback: 3);
        int transCol = FindColumnIndex(header, "TranslatedText", fallback: 4);

        var entries = new List<TextEntry>();
        for (int r = 1; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            if (keyCol >= row.Count || transCol >= row.Count)
            {
                continue;
            }

            string key = row[keyCol].Trim();
            string translated = row[transCol];
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(translated) || IsExcelFormulaError(translated))
            {
                continue;
            }

            string id = idCol < row.Count ? row[idCol].Trim() : $"entry_{r:D4}";
            string ctx = ctxCol < row.Count ? row[ctxCol] : string.Empty;
            string orig = origCol < row.Count ? row[origCol] : string.Empty;

            entries.Add(new TextEntry
            {
                Id = id,
                Key = key,
                Context = ctx,
                OriginalText = orig,
                TranslatedText = translated,
                Location = BuildFallbackLocation(target, key)
            });
        }

        TranslationPackage pkg = CreateEmptyPackageForTarget(target, profile);
        pkg.Entries.AddRange(entries);
        return pkg;
    }

    private static EntryLocation BuildFallbackLocation(ScannedTarget target, string key)
    {
        long? pathId = target.PrimaryPathId;
        string fieldKey = key;

        // Parse PathId=<id>::<fieldPath> from MonoBehaviour keys
        if (key.StartsWith("PathId=", StringComparison.OrdinalIgnoreCase))
        {
            int sep = key.IndexOf("::", StringComparison.Ordinal);
            if (sep > 7 && long.TryParse(key.AsSpan(7, sep - 7), out long parsedPid))
            {
                pathId = parsedPid;
                fieldKey = key[(sep + 2)..];
            }
        }

        int? metadataLitIdx = null;
        if (key.StartsWith("il2cpp:literal[", StringComparison.OrdinalIgnoreCase) && key.EndsWith(']'))
        {
            if (int.TryParse(key.AsSpan("il2cpp:literal[".Length, key.Length - "il2cpp:literal[".Length - 1), out int litIdx))
            {
                metadataLitIdx = litIdx;
            }
        }

        return new EntryLocation
        {
            ContainerRelativePath = target.ContainerRelativePath,
            BundleMemberName = target.BundleMemberName,
            AssetClassId = target.SourceKind switch
            {
                TextSourceKind.TextAsset => 49,
                TextSourceKind.MonoBehaviour => 114,
                _ => null
            },
            PathId = pathId,
            ScriptClassName = target.ScriptClassName,
            FieldOrInnerKey = fieldKey,
            MetadataLiteralIndex = metadataLitIdx
        };
    }

    private static TranslationPackage CreateEmptyPackageForTarget(ScannedTarget target, GameProfile profile) =>
        new()
        {
            Engine = profile.EngineName,
            EngineVersion = profile.EngineVersion,
            ScriptingBackend = profile.ScriptingBackend,
            TargetId = target.TargetId,
            DisplayName = target.DisplayName,
            SourceKind = target.SourceKind,
            ContainerRelativePath = target.ContainerRelativePath,
            BundleMemberName = target.BundleMemberName,
            OriginalContainerSha256 = string.Empty,
            InnerFormat = target.DetectedFormat,
            Entries = []
        };

    private static int FindColumnIndex(List<string> header, string columnName, int fallback)
    {
        for (int i = 0; i < header.Count; i++)
        {
            if (header[i].Trim().Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return fallback;
    }

    private static string? TryGetBasePathWithoutKnownSuffix(string filePath)
    {
        if (filePath.EndsWith(".translator.json", StringComparison.OrdinalIgnoreCase))
        {
            return filePath[..^".translator.json".Length];
        }

        if (filePath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return filePath[..^".manifest.json".Length];
        }

        if (filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
            !filePath.Contains(".raw.", StringComparison.OrdinalIgnoreCase))
        {
            return filePath[..^".csv".Length];
        }

        return null;
    }

    private static string? TryFindCompanionManifestForRawFile(string rawFilePath)
    {
        int rawMarker = rawFilePath.LastIndexOf(".raw.", StringComparison.OrdinalIgnoreCase);
        if (rawMarker <= 0)
        {
            return null;
        }

        string basePath = rawFilePath[..rawMarker];
        string candTranslator = $"{basePath}.translator.json";
        if (File.Exists(candTranslator))
        {
            return candTranslator;
        }

        string candManifest = $"{basePath}.manifest.json";
        if (File.Exists(candManifest))
        {
            return candManifest;
        }

        return null;
    }

    private static string DecodeTextPreservingBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
