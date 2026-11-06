namespace Translator.Core.Models;

public sealed record BackupVaultEntry(
    string ContainerRelativePath,
    string BackupRelativePath,
    string OriginalSha256,
    long FileSizeBytes,
    DateTimeOffset BackedUpAtUtc);

public sealed class BackupVaultManifest
{
    public string SchemaVersion { get; init; } = "1.0";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public required string GameName { get; init; }
    public required string GameRootPath { get; init; }
    public List<BackupVaultEntry> Entries { get; init; } = [];
}

public sealed record VaultStatus(
    bool HasBackup,
    int BackedUpFileCount,
    long TotalSizeBytes,
    DateTimeOffset? LastBackupAtUtc,
    IReadOnlyList<BackupVaultEntry> Entries);

public sealed record RestoreSummary(
    int RestoredFileCount,
    long TotalBytesRestored,
    IReadOnlyList<string> RestoredRelativePaths,
    IReadOnlyList<string> Errors);

public sealed record PatchPayload
{
    public required string SourceFilePath { get; init; }
    public required TranslationPackage Package { get; init; }
    public string? RawTextOverride { get; init; }
    public byte[]? RawBytesOverride { get; init; }

    public bool IsRawOverride => RawTextOverride is not null || RawBytesOverride is not null;

    public int TranslatedEntryCount =>
        IsRawOverride
            ? Math.Max(1, Package.Entries.Count)
            : Package.Entries.Count(e => !string.IsNullOrEmpty(e.TranslatedText));
}

public sealed record PatchOptions
{
    public bool VerifyOriginalSha256 { get; init; } = true;
}

public sealed record PatchSummary(
    int PatchedContainerCount,
    int PatchedTargetCount,
    int AppliedEntryCount,
    IReadOnlyList<string> PatchedContainers,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
