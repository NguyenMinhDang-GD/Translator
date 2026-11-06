using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Translator.Core.Abstractions;
using Translator.Core.Exporting;
using Translator.Core.Models;

namespace Translator.Core.Backup;

public sealed class BackupVaultService : IBackupVaultService
{
    public const string VaultFolderName = ".translator_backup";
    public const string VaultManifestFileName = "vault_manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SemaphoreSlim _manifestLock = new(1, 1);

    public string GetVaultRootDirectory(GameProfile profile) =>
        Path.Combine(profile.RootPath, VaultFolderName);

    public string GetOriginalFilePath(GameProfile profile, string containerRelativePath)
    {
        string normalized = NormalizeRelativePath(containerRelativePath);
        string vaultRoot = GetVaultRootDirectory(profile);
        string backupPath = Path.Combine(vaultRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(backupPath))
        {
            return backupPath;
        }

        return Path.Combine(profile.RootPath, normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    public async Task<VaultStatus> GetVaultStatusAsync(
        GameProfile profile,
        CancellationToken cancellationToken = default)
    {
        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BackupVaultManifest? manifest = await LoadManifestInternalAsync(profile, cancellationToken).ConfigureAwait(false);
            if (manifest is null || manifest.Entries.Count == 0)
            {
                return new VaultStatus(
                    HasBackup: false,
                    BackedUpFileCount: 0,
                    TotalSizeBytes: 0,
                    LastBackupAtUtc: null,
                    Entries: []);
            }

            string vaultRoot = GetVaultRootDirectory(profile);
            var existingEntries = new List<BackupVaultEntry>(manifest.Entries.Count);
            long totalBytes = 0;
            DateTimeOffset? latest = null;

            foreach (BackupVaultEntry entry in manifest.Entries)
            {
                string fullBackupPath = Path.Combine(vaultRoot, entry.BackupRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullBackupPath))
                {
                    existingEntries.Add(entry);
                    totalBytes += entry.FileSizeBytes;
                    if (!latest.HasValue || entry.BackedUpAtUtc > latest.Value)
                    {
                        latest = entry.BackedUpAtUtc;
                    }
                }
            }

            return new VaultStatus(
                HasBackup: existingEntries.Count > 0,
                BackedUpFileCount: existingEntries.Count,
                TotalSizeBytes: totalBytes,
                LastBackupAtUtc: latest,
                Entries: existingEntries);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    public async Task<BackupVaultEntry> EnsureFileBackedUpAsync(
        GameProfile profile,
        string containerRelativePath,
        string? expectedOriginalSha256 = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedRel = NormalizeRelativePath(containerRelativePath);
        string vaultRoot = GetVaultRootDirectory(profile);
        string backupFullPath = Path.Combine(vaultRoot, normalizedRel.Replace('/', Path.DirectorySeparatorChar));
        string gameFileFullPath = Path.Combine(profile.RootPath, normalizedRel.Replace('/', Path.DirectorySeparatorChar));

        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(vaultRoot);
            BackupVaultManifest manifest = await LoadManifestInternalAsync(profile, cancellationToken).ConfigureAwait(false)
                ?? new BackupVaultManifest
                {
                    GameName = profile.GameName,
                    GameRootPath = profile.RootPath
                };

            BackupVaultEntry? existing = manifest.Entries.FirstOrDefault(e =>
                string.Equals(NormalizeRelativePath(e.ContainerRelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase));

            // Immutability rule: If backup already exists in vault, NEVER overwrite it!
            if (existing is not null && File.Exists(backupFullPath))
            {
                string currentBackupHash = HashUtility.ComputeFileSha256(backupFullPath);
                if (!string.Equals(currentBackupHash, existing.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"File sao lưu trong kho .translator_backup bị sai lệch mã băm SHA-256 ({normalizedRel}). " +
                        $"Kỳ vọng: {existing.OriginalSha256}, Thực tế: {currentBackupHash}.");
                }

                return existing;
            }

            if (!File.Exists(gameFileFullPath))
            {
                throw new FileNotFoundException($"Không tìm thấy file gốc của game để sao lưu: {gameFileFullPath}", gameFileFullPath);
            }

            string gameFileSha256 = HashUtility.ComputeFileSha256(gameFileFullPath);
            long fileSize = new FileInfo(gameFileFullPath).Length;

            string? backupParentDir = Path.GetDirectoryName(backupFullPath);
            if (!string.IsNullOrEmpty(backupParentDir))
            {
                Directory.CreateDirectory(backupParentDir);
            }

            string tempCopyPath = $"{backupFullPath}.vault.tmp";
            await CopyFileWithFlushAsync(gameFileFullPath, tempCopyPath, cancellationToken).ConfigureAwait(false);

            string copiedSha256 = HashUtility.ComputeFileSha256(tempCopyPath);
            if (!string.Equals(gameFileSha256, copiedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(tempCopyPath);
                throw new IOException($"Đối chiếu SHA-256 thất bại khi tạo bản sao lưu cho {normalizedRel}.");
            }

            File.Move(tempCopyPath, backupFullPath, overwrite: true);

            var newEntry = new BackupVaultEntry(
                ContainerRelativePath: normalizedRel,
                BackupRelativePath: normalizedRel,
                OriginalSha256: gameFileSha256,
                FileSizeBytes: fileSize,
                BackedUpAtUtc: DateTimeOffset.UtcNow);

            if (existing is not null)
            {
                manifest.Entries.Remove(existing);
            }

            manifest.Entries.Add(newEntry);
            manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await SaveManifestInternalAsync(profile, manifest, cancellationToken).ConfigureAwait(false);
            return newEntry;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    public async Task<RestoreSummary> RestoreOriginalAsync(
        GameProfile profile,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string vaultRoot = GetVaultRootDirectory(profile);
            BackupVaultManifest? manifest = await LoadManifestInternalAsync(profile, cancellationToken).ConfigureAwait(false);
            if (manifest is null || manifest.Entries.Count == 0 || !Directory.Exists(vaultRoot))
            {
                return new RestoreSummary(0, 0, [], ["Chưa có dữ liệu sao lưu trong kho .translator_backup/."]);
            }

            var restoredPaths = new List<string>();
            var errors = new List<string>();
            long totalBytesRestored = 0;
            int total = manifest.Entries.Count;
            int current = 0;

            foreach (BackupVaultEntry entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current++;
                progress?.Report(new ScanProgress("Restore", current, total, entry.ContainerRelativePath));

                string backupFullPath = Path.Combine(vaultRoot, entry.BackupRelativePath.Replace('/', Path.DirectorySeparatorChar));
                string targetGamePath = Path.Combine(profile.RootPath, entry.ContainerRelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(backupFullPath))
                {
                    errors.Add($"Thiếu file sao lưu trong kho: {entry.BackupRelativePath}");
                    continue;
                }

                try
                {
                    string backupHash = HashUtility.ComputeFileSha256(backupFullPath);
                    if (!string.Equals(backupHash, entry.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"File sao lưu '{entry.ContainerRelativePath}' không khớp SHA-256 gốc (Kỳ vọng {entry.OriginalSha256}, thực tế {backupHash}).");
                        continue;
                    }

                    string? targetDir = Path.GetDirectoryName(targetGamePath);
                    if (!string.IsNullOrEmpty(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    string tempRestorePath = $"{targetGamePath}.restore.tmp";
                    await CopyFileWithFlushAsync(backupFullPath, tempRestorePath, cancellationToken).ConfigureAwait(false);
                    File.Move(tempRestorePath, targetGamePath, overwrite: true);

                    string restoredHash = HashUtility.ComputeFileSha256(targetGamePath);
                    if (!string.Equals(restoredHash, entry.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"Đối chiếu SHA-256 sau khi khôi phục thất bại: {entry.ContainerRelativePath}");
                        continue;
                    }

                    restoredPaths.Add(entry.ContainerRelativePath);
                    totalBytesRestored += entry.FileSizeBytes;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{entry.ContainerRelativePath}: {ex.Message}");
                }
            }

            return new RestoreSummary(
                RestoredFileCount: restoredPaths.Count,
                TotalBytesRestored: totalBytesRestored,
                RestoredRelativePaths: restoredPaths,
                Errors: errors);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    private async Task<BackupVaultManifest?> LoadManifestInternalAsync(
        GameProfile profile,
        CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(GetVaultRootDirectory(profile), VaultManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BackupVaultManifest>(json, JsonOptions);
    }

    private async Task SaveManifestInternalAsync(
        GameProfile profile,
        BackupVaultManifest manifest,
        CancellationToken cancellationToken)
    {
        string vaultRoot = GetVaultRootDirectory(profile);
        Directory.CreateDirectory(vaultRoot);
        string manifestPath = Path.Combine(vaultRoot, VaultManifestFileName);
        string tempPath = $"{manifestPath}.tmp";

        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, manifestPath, overwrite: true);
    }

    private static async Task CopyFileWithFlushAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 65536,
            useAsync: true);

        await using var destStream = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536,
            useAsync: true);

        await sourceStream.CopyToAsync(destStream, 65536, cancellationToken).ConfigureAwait(false);
        await destStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    public static string NormalizeRelativePath(string relativePath) =>
        relativePath.Trim().Replace('\\', '/').TrimStart('/');
}
