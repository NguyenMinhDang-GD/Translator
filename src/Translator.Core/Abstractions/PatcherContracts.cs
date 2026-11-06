using Translator.Core.Models;

namespace Translator.Core.Abstractions;

public interface IInnerContentReconstructor
{
    string Reconstruct(
        string originalContent,
        IReadOnlyDictionary<string, string> translations);
}

public interface IBackupVaultService
{
    string GetVaultRootDirectory(GameProfile profile);

    Task<VaultStatus> GetVaultStatusAsync(
        GameProfile profile,
        CancellationToken cancellationToken = default);

    Task<BackupVaultEntry> EnsureFileBackedUpAsync(
        GameProfile profile,
        string containerRelativePath,
        string? expectedOriginalSha256 = null,
        CancellationToken cancellationToken = default);

    string GetOriginalFilePath(
        GameProfile profile,
        string containerRelativePath);

    Task<RestoreSummary> RestoreOriginalAsync(
        GameProfile profile,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IGameEnginePatcher
{
    string EngineName { get; }

    Task<PatchSummary> PatchAsync(
        GameProfile profile,
        IReadOnlyList<PatchPayload> payloads,
        IBackupVaultService backupVault,
        PatchOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IPatchPackageLoader
{
    Task<IReadOnlyList<PatchPayload>> LoadFromDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PatchPayload>> LoadFromFileAsync(
        string filePath,
        string? optionalManifestPath = null,
        ScannedTarget? fallbackTarget = null,
        GameProfile? fallbackProfile = null,
        CancellationToken cancellationToken = default);
}
