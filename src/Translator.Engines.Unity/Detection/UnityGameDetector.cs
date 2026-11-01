using System.Text;
using System.Text.RegularExpressions;
using Translator.Core.Models;

namespace Translator.Engines.Unity.Detection;

public static partial class UnityGameDetector
{
    [GeneratedRegex(@"\b(20\d{2}\.\d+\.\d+[a-z]\d+|6000\.\d+\.\d+[a-z]\d+|[45]\.\d+\.\d+[a-z]\d+)\b", RegexOptions.Compiled)]
    private static partial Regex UnityVersionRegex();

    public static GameProfile? Detect(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(selectedPath);
        string? dataFolder = ResolveDataFolder(fullPath, out string rootFolder, out string gameName);

        if (dataFolder is null)
        {
            return null;
        }

        string managedFolder = Path.Combine(dataFolder, "Managed");
        bool hasManaged = Directory.Exists(managedFolder) &&
                          Directory.EnumerateFiles(managedFolder, "*.dll").Any();

        string metadataCandidate1 = Path.Combine(dataFolder, "il2cpp_data", "Metadata", "global-metadata.dat");
        string metadataCandidate2 = Path.Combine(rootFolder, "il2cpp_data", "Metadata", "global-metadata.dat");
        string? metadataPath = File.Exists(metadataCandidate1)
            ? metadataCandidate1
            : (File.Exists(metadataCandidate2) ? metadataCandidate2 : null);

        string? gameAssemblyPath = FindGameAssembly(rootFolder, dataFolder);

        string backend;
        if (metadataPath is not null || gameAssemblyPath is not null)
        {
            backend = "IL2CPP";
        }
        else if (hasManaged)
        {
            backend = "Mono";
        }
        else
        {
            backend = "Unknown";
        }

        string unityVersion = TryReadUnityVersion(dataFolder, rootFolder) ?? "Unknown";

        return new GameProfile(
            GameName: gameName,
            RootPath: rootFolder,
            DataFolderPath: dataFolder,
            EngineName: "Unity",
            EngineVersion: unityVersion,
            ScriptingBackend: backend,
            ManagedFolderPath: hasManaged ? managedFolder : null,
            GlobalMetadataPath: metadataPath,
            GameAssemblyPath: gameAssemblyPath);
    }

    private static string? ResolveDataFolder(string fullPath, out string rootFolder, out string gameName)
    {
        string dirName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // Case 1: User selected the *_Data directory directly
        if (dirName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) && LooksLikeUnityDataFolder(fullPath))
        {
            rootFolder = Directory.GetParent(fullPath)?.FullName ?? fullPath;
            gameName = dirName[..^5];
            return fullPath;
        }

        // Case 2: Standard root directory containing <Game>_Data
        foreach (string subDir in Directory.EnumerateDirectories(fullPath, "*_Data", SearchOption.TopDirectoryOnly))
        {
            if (LooksLikeUnityDataFolder(subDir))
            {
                rootFolder = fullPath;
                string subName = Path.GetFileName(subDir);
                gameName = subName[..^5];
                return subDir;
            }
        }

        // Case 3: User selected a folder whose files directly represent extracted Unity data (e.g., Android assets/bin/Data or test folder)
        if (LooksLikeUnityDataFolder(fullPath))
        {
            rootFolder = fullPath;
            gameName = string.IsNullOrWhiteSpace(dirName) ? "UnityGame" : dirName;
            return fullPath;
        }

        rootFolder = fullPath;
        gameName = string.Empty;
        return null;
    }

    private static bool LooksLikeUnityDataFolder(string folder)
    {
        if (File.Exists(Path.Combine(folder, "globalgamemanagers")) ||
            File.Exists(Path.Combine(folder, "data.unity3d")) ||
            File.Exists(Path.Combine(folder, "mainData")) ||
            File.Exists(Path.Combine(folder, "resources.assets")) ||
            File.Exists(Path.Combine(folder, "sharedassets0.assets")) ||
            Directory.Exists(Path.Combine(folder, "Managed")) ||
            Directory.Exists(Path.Combine(folder, "il2cpp_data")) ||
            Directory.Exists(Path.Combine(folder, "StreamingAssets")))
        {
            return true;
        }

        return Directory.EnumerateFiles(folder, "*.bundle", SearchOption.TopDirectoryOnly).Any() ||
               Directory.EnumerateFiles(folder, "*.assets", SearchOption.TopDirectoryOnly).Any();
    }

    private static string? FindGameAssembly(string rootFolder, string dataFolder)
    {
        string[] candidates =
        [
            Path.Combine(rootFolder, "GameAssembly.dll"),
            Path.Combine(rootFolder, "GameAssembly.so"),
            Path.Combine(rootFolder, "libil2cpp.so"),
            Path.Combine(dataFolder, "Native", "GameAssembly.dll")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? TryReadUnityVersion(string dataFolder, string rootFolder)
    {
        string[] probeFiles =
        [
            Path.Combine(dataFolder, "globalgamemanagers"),
            Path.Combine(dataFolder, "mainData"),
            Path.Combine(dataFolder, "data.unity3d"),
            Path.Combine(dataFolder, "resources.assets"),
            Path.Combine(dataFolder, "sharedassets0.assets"),
            Path.Combine(rootFolder, "UnityPlayer.dll")
        ];

        byte[] buffer = new byte[4096];
        foreach (string file in probeFiles)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int read = fs.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    continue;
                }

                string ascii = Encoding.Latin1.GetString(buffer, 0, read);
                Match m = UnityVersionRegex().Match(ascii);
                if (m.Success)
                {
                    return m.Value;
                }
            }
            catch
            {
                // Ignore locked or unreadable probe file
            }
        }

        return null;
    }
}
