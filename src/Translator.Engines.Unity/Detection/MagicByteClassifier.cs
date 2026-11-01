using System.Buffers.Binary;
using System.Text;
using AssetsTools.NET;
using Translator.Core.Filters;
using Translator.Core.Models;

namespace Translator.Engines.Unity.Detection;

public enum ClassifiedUnityFileKind
{
    Ignore,
    LooseTextFile,
    SerializedAssetsFile,
    AssetBundleFile,
    ManagedAssemblyDll,
    Il2CppGlobalMetadata,
    SuspectedEncrypted
}

public sealed record ClassifiedUnityFile(
    string FullPath,
    string RelativePath,
    long SizeBytes,
    ClassifiedUnityFileKind Kind,
    string? WarningReason = null);

public static class MagicByteClassifier
{
    private static readonly HashSet<string> LooseTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".csv", ".tsv", ".xml", ".txt", ".ini",
        ".yaml", ".yml", ".cfg", ".lang", ".po", ".properties"
    };

    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ress", ".resource", ".exe", ".pdb", ".mdb", ".ilk", ".exp", ".lib",
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".ico", ".gif", ".webp",
        ".wav", ".ogg", ".mp3", ".bank", ".fsb", ".mp4", ".webm", ".ogv", ".mov",
        ".ttf", ".otf", ".ttc", ".shader", ".manifest", ".meta", ".hash", ".bak", ".tmp"
    };

    private static readonly HashSet<string> IgnoredUnitySystemJsonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "RuntimeInitializeOnLoads.json",
        "ScriptingAssemblies.json",
        "UnityServicesProjectConfiguration.json",
        "BillingMode.json",
        "settings.json",
        "boot.config",
        "app.info"
    };

    private static readonly string[] SystemDllPrefixes =
    [
        "System.", "System,", "Microsoft.", "UnityEngine", "Unity.",
        "Mono.", "mscorlib", "netstandard", "Newtonsoft.", "DOTween",
        "DG.Tweening", "UniTask", "Cysharp.", "FMOD", "Steamworks",
        "com.rlabrecque", "Ionic.", "ICSharpCode.", "I18N", "Boo.Lang",
        "SyntaxTree.", "nunit.", "Google.Protobuf", "Sqlite", "MonoPosix"
    ];

    public static IReadOnlyList<ClassifiedUnityFile> ScanAndClassifyDirectory(
        GameProfile profile,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ClassifiedUnityFile>();
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Scan DataFolder and Root loose localization folders safely
        ScanDirectoryRecursive(profile.DataFolderPath, profile, results, visitedPaths, cancellationToken);

        // Also inspect top-level localization/lang folders in RootPath if separate from *_Data
        if (!string.Equals(profile.RootPath, profile.DataFolderPath, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(profile.RootPath))
        {
            foreach (string subDir in Directory.EnumerateDirectories(profile.RootPath, "*", SearchOption.TopDirectoryOnly))
            {
                string subName = Path.GetFileName(subDir);
                if (subName.StartsWith(".translator", StringComparison.OrdinalIgnoreCase) ||
                    subName.Equals("MonoBleedingEdge", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(subDir, profile.DataFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (subName.Contains("lang", StringComparison.OrdinalIgnoreCase) ||
                    subName.Contains("loc", StringComparison.OrdinalIgnoreCase) ||
                    subName.Contains("data", StringComparison.OrdinalIgnoreCase) ||
                    subName.Contains("bundle", StringComparison.OrdinalIgnoreCase))
                {
                    ScanDirectoryRecursive(subDir, profile, results, visitedPaths, cancellationToken);
                }
            }
        }

        return results;
    }

    private static void ScanDirectoryRecursive(
        string directory,
        GameProfile profile,
        List<ClassifiedUnityFile> results,
        HashSet<string> visitedPaths,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string currentDir = stack.Pop();
            string dirName = Path.GetFileName(currentDir);

            if (dirName.StartsWith(".translator", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("MonoBleedingEdge", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("UnitySubsystems", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("Plugins", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(currentDir);
            }
            catch
            {
                continue;
            }

            foreach (string sub in subDirs)
            {
                stack.Push(sub);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDir);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visitedPaths.Add(file))
                {
                    continue;
                }

                ClassifiedUnityFile classified = ClassifySingleFile(file, profile);
                if (classified.Kind != ClassifiedUnityFileKind.Ignore)
                {
                    results.Add(classified);
                }
            }
        }
    }

    public static ClassifiedUnityFile ClassifySingleFile(string fullPath, GameProfile profile)
    {
        string relativePath = Path.GetRelativePath(profile.RootPath, fullPath);
        FileInfo fi;
        try
        {
            fi = new FileInfo(fullPath);
            if (!fi.Exists || fi.Length == 0)
            {
                return new ClassifiedUnityFile(fullPath, relativePath, 0, ClassifiedUnityFileKind.Ignore);
            }
        }
        catch
        {
            return new ClassifiedUnityFile(fullPath, relativePath, 0, ClassifiedUnityFileKind.Ignore);
        }

        string fileName = fi.Name;
        string ext = fi.Extension;

        // Skip multi-part split assets (.split1, .split2, etc.) except .split0
        if (ext.StartsWith(".split", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".split0", StringComparison.OrdinalIgnoreCase))
        {
            return new ClassifiedUnityFile(fullPath, relativePath, fi.Length, ClassifiedUnityFileKind.Ignore);
        }

        if (SkipExtensions.Contains(ext))
        {
            return new ClassifiedUnityFile(fullPath, relativePath, fi.Length, ClassifiedUnityFileKind.Ignore);
        }

        // 1. IL2CPP global-metadata.dat
        if (fileName.Equals("global-metadata.dat", StringComparison.OrdinalIgnoreCase))
        {
            return InspectGlobalMetadata(fullPath, relativePath, fi.Length);
        }

        // 2. Managed .NET Assemblies
        if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            if (profile.ManagedFolderPath is not null &&
                fullPath.StartsWith(profile.ManagedFolderPath, StringComparison.OrdinalIgnoreCase) &&
                IsCandidateGameAssembly(fileName))
            {
                return new ClassifiedUnityFile(fullPath, relativePath, fi.Length, ClassifiedUnityFileKind.ManagedAssemblyDll);
            }

            return new ClassifiedUnityFile(fullPath, relativePath, fi.Length, ClassifiedUnityFileKind.Ignore);
        }

        // Read first 32 magic bytes
        Span<byte> header = stackalloc byte[32];
        int bytesRead = TryReadHeaderBytes(fullPath, header);
        if (bytesRead <= 0)
        {
            return new ClassifiedUnityFile(fullPath, relativePath, fi.Length, ClassifiedUnityFileKind.Ignore);
        }

        ReadOnlySpan<byte> magic = header[..bytesRead];

        // 3. Magic Byte Check for Unity AssetBundle (catches .bundle, .unity3d, .dat, .bin, extensionless!)
        if (HasUnityBundleMagic(magic))
        {
            return new ClassifiedUnityFile(fullPath, relativePath, fi.Length, ClassifiedUnityFileKind.AssetBundleFile);
        }

        // 4. Loose Text File check
        if (LooseTextExtensions.Contains(ext))
        {
            if (IgnoredUnitySystemJsonNames.Contains(fileName) ||
                (fileName.StartsWith("catalog_", StringComparison.OrdinalIgnoreCase) && ext.Equals(".json", StringComparison.OrdinalIgnoreCase)))
            {
                return new ClassifiedUnityFile(fullPath, relativePath, fi.Length, ClassifiedUnityFileKind.Ignore);
            }

            if (IsReadableTextFileSample(fullPath))
            {
                return new ClassifiedUnityFile(fullPath, relativePath, fi.Length, ClassifiedUnityFileKind.LooseTextFile);
            }
        }

        // 5. Unity Serialized AssetsFile (.assets, level0, sharedassets0.assets, resources.assets, globalgamemanagers.assets)
        if (ShouldCheckSerializedAssetsFile(fileName, ext, fi.Length))
        {
            try
            {
                if (AssetsFile.IsAssetsFile(fullPath))
                {
                    // Skip bare globalgamemanagers (engine settings without TextAsset/MonoBehaviour), keep globalgamemanagers.assets
                    if (fileName.Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ClassifiedUnityFile(fullPath, relativePath, fi.Length, ClassifiedUnityFileKind.Ignore);
                    }

                    return new ClassifiedUnityFile(fullPath, relativePath, fi.Length, ClassifiedUnityFileKind.SerializedAssetsFile);
                }
            }
            catch
            {
                // Fall through to suspected encryption check
            }
        }

        // 6. Suspected Encrypted Bundle / Asset Container check
        if (LooksLikeSuspectedEncryptedBundle(fileName, ext, relativePath, fi.Length))
        {
            string hexSig = Convert.ToHexString(magic[..Math.Min(8, magic.Length)]);
            return new ClassifiedUnityFile(
                fullPath,
                relativePath,
                fi.Length,
                ClassifiedUnityFileKind.SuspectedEncrypted,
                $"Header không khớp chuẩn UnityFS/SerializedFile (8 byte đầu: 0x{hexSig}) — nghi bị mã hóa hoặc gắn Fake Header.");
        }

        return new ClassifiedUnityFile(fullPath, relativePath, fi.Length, ClassifiedUnityFileKind.Ignore);
    }

    public static bool HasUnityBundleMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < 7)
        {
            return false;
        }

        return header.StartsWith("UnityFS\0"u8) ||
               header.StartsWith("UnityWeb\0"u8) ||
               header.StartsWith("UnityRaw\0"u8) ||
               header.StartsWith("UnityArchive\0"u8);
    }

    private static ClassifiedUnityFile InspectGlobalMetadata(string fullPath, string relativePath, long length)
    {
        Span<byte> header = stackalloc byte[8];
        int read = TryReadHeaderBytes(fullPath, header);
        if (read < 8)
        {
            return new ClassifiedUnityFile(
                fullPath,
                relativePath,
                length,
                ClassifiedUnityFileKind.SuspectedEncrypted,
                "File global-metadata.dat quá ngắn hoặc không thể đọc header.");
        }

        uint sanity = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4, 4));

        if (sanity == 0xFAB11BAFu && version is >= 15 and <= 50)
        {
            return new ClassifiedUnityFile(fullPath, relativePath, length, ClassifiedUnityFileKind.Il2CppGlobalMetadata);
        }

        return new ClassifiedUnityFile(
            fullPath,
            relativePath,
            length,
            ClassifiedUnityFileKind.SuspectedEncrypted,
            $"global-metadata.dat có Sanity=0x{sanity:X8} (chuẩn Unity là 0xFAB11BAF) — file đã bị mã hóa chống dịch ngược IL2CPP.");
    }

    private static bool IsCandidateGameAssembly(string dllFileName)
    {
        foreach (string prefix in SystemDllPrefixes)
        {
            if (dllFileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShouldCheckSerializedAssetsFile(string fileName, string ext, long size)
    {
        if (size < 40)
        {
            return false;
        }

        if (ext.Equals(".assets", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".sharedAssets", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".split0", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrEmpty(ext))
        {
            if (fileName.StartsWith("level", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("mainData", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("resources", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeSuspectedEncryptedBundle(string fileName, string ext, string relativePath, long size)
    {
        if (size < 64)
        {
            return false;
        }

        if (ext.Equals(".bundle", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".unity3d", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".assetbundle", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ab", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string normPath = relativePath.Replace('\\', '/');
        if ((normPath.Contains("StreamingAssets/aa/", StringComparison.OrdinalIgnoreCase) ||
             normPath.Contains("AssetBundles/", StringComparison.OrdinalIgnoreCase)) &&
            !LooseTextExtensions.Contains(ext) &&
            !ext.Equals(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsReadableTextFileSample(string fullPath)
    {
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int sampleLen = (int)Math.Min(fs.Length, 8192);
            if (sampleLen == 0)
            {
                return false;
            }

            byte[] buffer = new byte[sampleLen];
            int read = fs.Read(buffer, 0, sampleLen);
            // Note: A multi-byte UTF-8 sequence might be cut right at byte 8192; trim up to 3 trailing continuation bytes
            int validEnd = TrimIncompleteTrailingUtf8Sequence(buffer.AsSpan(0, read));
            return validEnd > 0 && TextHeuristicScorer.TryDecodeReadableText(buffer.AsSpan(0, validEnd), out _);
        }
        catch
        {
            return false;
        }
    }

    private static int TrimIncompleteTrailingUtf8Sequence(ReadOnlySpan<byte> span)
    {
        if (span.Length < 8192)
        {
            return span.Length;
        }

        // Walk back up to 3 bytes to see if there's an incomplete multi-byte UTF-8 starter at the cut boundary
        for (int back = 1; back <= 3 && back < span.Length; back++)
        {
            byte b = span[^back];
            if ((b & 0b1000_0000) == 0)
            {
                // ASCII byte — everything up to and including it is complete
                return span.Length;
            }

            if ((b & 0b1100_0000) == 0b1100_0000)
            {
                // Multi-byte starter found at distance 'back' from end
                int expectedLen = (b & 0b1110_0000) == 0b1100_0000 ? 2
                                : (b & 0b1111_0000) == 0b1110_0000 ? 3 : 4;
                return back < expectedLen ? span.Length - back : span.Length;
            }
        }

        return span.Length;
    }

    private static int TryReadHeaderBytes(string fullPath, Span<byte> destination)
    {
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return fs.Read(destination);
        }
        catch
        {
            return 0;
        }
    }
}
