using System.Text;
using Translator.Core.Abstractions;
using Translator.Core.Backup;
using Translator.Core.Filters;
using Translator.Core.Models;
using Translator.Core.Parsers;
using Translator.Engines.Unity.Detection;
using Translator.Engines.Unity.TypeTrees;

namespace Translator.Engines.Unity.Patching;

public sealed class UnityPatcher : IGameEnginePatcher
{
    private readonly InnerContentParserRegistry _parserRegistry;

    public UnityPatcher(InnerContentParserRegistry? parserRegistry = null)
    {
        _parserRegistry = parserRegistry ?? new InnerContentParserRegistry();
    }

    public string EngineName => "Unity";

    public async Task<PatchSummary> PatchAsync(
        GameProfile profile,
        IReadOnlyList<PatchPayload> payloads,
        IBackupVaultService backupVault,
        PatchOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PatchOptions();

        List<PatchPayload> activePayloads = payloads
            .Where(p => p.TranslatedEntryCount > 0)
            .ToList();

        if (activePayloads.Count == 0)
        {
            return new PatchSummary(
                PatchedContainerCount: 0,
                PatchedTargetCount: 0,
                AppliedEntryCount: 0,
                PatchedContainers: [],
                Warnings: ["Không có câu dịch nào khác rỗng trong các file đầu vào để vá."],
                Errors: []);
        }

        // Group payloads by normalized ContainerRelativePath so each container (.assets / .bundle / .dll / global-metadata.dat)
        // is backed up once and written atomically in a single pass!
        var groupedByContainer = new Dictionary<string, List<PatchPayload>>(StringComparer.OrdinalIgnoreCase);
        foreach (PatchPayload payload in activePayloads)
        {
            string rel = BackupVaultService.NormalizeRelativePath(payload.Package.ContainerRelativePath);
            if (string.IsNullOrWhiteSpace(rel))
            {
                rel = BackupVaultService.NormalizeRelativePath(
                    payload.Package.Entries.FirstOrDefault()?.Location.ContainerRelativePath ?? string.Empty);
            }

            if (string.IsNullOrWhiteSpace(rel))
            {
                continue;
            }

            if (!groupedByContainer.TryGetValue(rel, out List<PatchPayload>? list))
            {
                list = [];
                groupedByContainer[rel] = list;
            }

            list.Add(payload);
        }

        var patchedContainers = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();
        int patchedTargets = 0;
        int totalAppliedEntries = 0;

        int totalContainers = groupedByContainer.Count;
        int currentContainerIdx = 0;

        using var resolver = new TypeTreeResolver(profile);

        foreach ((string containerRelPath, List<PatchPayload> containerPayloads) in groupedByContainer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentContainerIdx++;
            progress?.Report(new ScanProgress("Patch", currentContainerIdx, totalContainers, containerRelPath));

            try
            {
                string? expectedSha256 = containerPayloads
                    .Select(p => p.Package.OriginalContainerSha256)
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

                // 1. Ensure immutable backup exists in .translator_backup/ before touching anything
                BackupVaultEntry vaultEntry = await backupVault
                    .EnsureFileBackedUpAsync(profile, containerRelPath, expectedSha256, cancellationToken)
                    .ConfigureAwait(false);

                if (options.VerifyOriginalSha256 &&
                    !string.IsNullOrWhiteSpace(expectedSha256) &&
                    !string.Equals(vaultEntry.OriginalSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(
                        $"Cảnh báo SHA-256 tại '{containerRelPath}': Mã băm gốc trong kho ({vaultEntry.OriginalSha256[..12]}...) " +
                        $"khác với lúc trích xuất ({expectedSha256[..12]}...). Đã áp dụng bản dịch trên nền file gốc trong kho.");
                }

                // 2. Always read base container from immutable backup in .translator_backup/
                string backupFilePath = backupVault.GetOriginalFilePath(profile, containerRelPath);
                string targetGameFilePath = Path.Combine(
                    profile.RootPath,
                    containerRelPath.Replace('/', Path.DirectorySeparatorChar));

                string? parentDir = Path.GetDirectoryName(targetGameFilePath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                ClassifiedUnityFile classified = MagicByteClassifier.ClassifySingleFile(backupFilePath, profile);
                int appliedInContainer = 0;

                if (containerPayloads.All(p => p.Package.SourceKind == TextSourceKind.LooseFile) ||
                    classified.Kind == ClassifiedUnityFileKind.LooseTextFile)
                {
                    appliedInContainer = await PatchLooseFileAtomicAsync(
                        backupFilePath,
                        targetGameFilePath,
                        containerPayloads,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (classified.Kind == ClassifiedUnityFileKind.Il2CppGlobalMetadata ||
                         containerRelPath.EndsWith("global-metadata.dat", StringComparison.OrdinalIgnoreCase))
                {
                    appliedInContainer = await Il2CppMetadataPatcher.PatchFileAtomicAsync(
                        backupFilePath,
                        targetGameFilePath,
                        containerPayloads,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (classified.Kind == ClassifiedUnityFileKind.ManagedAssemblyDll ||
                         containerRelPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    appliedInContainer = await MonoCecilDllPatcher.PatchFileAtomicAsync(
                        backupFilePath,
                        targetGameFilePath,
                        containerPayloads,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (classified.Kind == ClassifiedUnityFileKind.AssetBundleFile)
                {
                    appliedInContainer = await Task.Run(() =>
                        UnityAssetContainerPatcher.PatchAssetBundleFileAtomic(
                            profile,
                            backupFilePath,
                            targetGameFilePath,
                            containerPayloads,
                            resolver,
                            _parserRegistry,
                            cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // SerializedAssetsFile (.assets, level*, globalgamemanagers, etc.)
                    appliedInContainer = await Task.Run(() =>
                        UnityAssetContainerPatcher.PatchSerializedAssetsFileAtomic(
                            profile,
                            backupFilePath,
                            targetGameFilePath,
                            containerPayloads,
                            resolver,
                            _parserRegistry,
                            cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }

                if (appliedInContainer > 0)
                {
                    patchedContainers.Add(containerRelPath);
                    patchedTargets += containerPayloads.Count;
                    totalAppliedEntries += appliedInContainer;
                }
                else
                {
                    warnings.Add($"Không có thay đổi nào được áp dụng cho '{containerRelPath}'.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{containerRelPath}: {ex.Message}");
            }
        }

        return new PatchSummary(
            PatchedContainerCount: patchedContainers.Count,
            PatchedTargetCount: patchedTargets,
            AppliedEntryCount: totalAppliedEntries,
            PatchedContainers: patchedContainers,
            Warnings: warnings,
            Errors: errors);
    }

    private async Task<int> PatchLooseFileAtomicAsync(
        string backupFilePath,
        string targetGameFilePath,
        IReadOnlyList<PatchPayload> payloads,
        CancellationToken cancellationToken)
    {
        byte[] originalBytes = await File.ReadAllBytesAsync(backupFilePath, cancellationToken).ConfigureAwait(false);
        PatchPayload primary = payloads[0];

        byte[] outputBytes;
        int appliedCount = 0;

        if (primary.IsRawOverride)
        {
            if (primary.RawBytesOverride is not null)
            {
                outputBytes = primary.RawBytesOverride;
                appliedCount = Math.Max(1, primary.Package.Entries.Count);
            }
            else if (primary.RawTextOverride is not null)
            {
                outputBytes = EncodeMatchingBom(primary.RawTextOverride, originalBytes);
                appliedCount = Math.Max(1, primary.Package.Entries.Count);
            }
            else
            {
                return 0;
            }
        }
        else
        {
            if (!TextHeuristicScorer.TryDecodeReadableText(originalBytes, out string originalText))
            {
                throw new InvalidDataException($"File văn bản gốc '{Path.GetFileName(backupFilePath)}' không thể giải mã UTF-8/UTF-16.");
            }

            var translations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (PatchPayload p in payloads)
            {
                foreach (TextEntry e in p.Package.Entries)
                {
                    if (string.IsNullOrEmpty(e.TranslatedText))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(e.Location.FieldOrInnerKey))
                    {
                        translations[e.Location.FieldOrInnerKey] = e.TranslatedText;
                    }

                    if (!string.IsNullOrWhiteSpace(e.Key))
                    {
                        translations[e.Key] = e.TranslatedText;
                    }

                    appliedCount++;
                }
            }

            if (translations.Count == 0)
            {
                return 0;
            }

            string reconstructed = _parserRegistry.Reconstruct(primary.Package.InnerFormat, originalText, translations);
            outputBytes = EncodeMatchingBom(reconstructed, originalBytes);
        }

        string tempPath = $"{targetGameFilePath}.translator.tmp";
        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        {
            await fs.WriteAsync(outputBytes, cancellationToken).ConfigureAwait(false);
            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, targetGameFilePath, overwrite: true);
        return appliedCount;
    }

    private static byte[] EncodeMatchingBom(string text, ReadOnlySpan<byte> originalBytes)
    {
        if (originalBytes.Length >= 3 &&
            originalBytes[0] == 0xEF &&
            originalBytes[1] == 0xBB &&
            originalBytes[2] == 0xBF)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            byte[] result = new byte[body.Length + 3];
            result[0] = 0xEF;
            result[1] = 0xBB;
            result[2] = 0xBF;
            body.CopyTo(result.AsSpan(3));
            return result;
        }

        if (originalBytes.Length >= 2 && originalBytes[0] == 0xFF && originalBytes[1] == 0xFE)
        {
            byte[] body = Encoding.Unicode.GetBytes(text);
            byte[] result = new byte[body.Length + 2];
            result[0] = 0xFF;
            result[1] = 0xFE;
            body.CopyTo(result.AsSpan(2));
            return result;
        }

        if (originalBytes.Length >= 2 && originalBytes[0] == 0xFE && originalBytes[1] == 0xFF)
        {
            byte[] body = Encoding.BigEndianUnicode.GetBytes(text);
            byte[] result = new byte[body.Length + 2];
            result[0] = 0xFE;
            result[1] = 0xFF;
            body.CopyTo(result.AsSpan(2));
            return result;
        }

        return Encoding.UTF8.GetBytes(text);
    }
}
