using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Translator.Engines.Unity.Detection;

namespace Translator.Engines.Unity.Handlers;

internal sealed class OpenedAssetsContainer : IDisposable
{
    private readonly AssetsManager _am;
    private readonly Stream? _ownedPrimaryStream;
    private readonly Stream? _ownedUnpackedTempStream;
    public BundleFileInstance? BundleInstance { get; }
    public IReadOnlyList<(string? BundleMemberName, AssetsFileInstance FileInst)> AssetsFiles { get; }

    private OpenedAssetsContainer(
        AssetsManager am,
        Stream? ownedPrimaryStream,
        Stream? ownedUnpackedTempStream,
        BundleFileInstance? bundleInst,
        IReadOnlyList<(string? BundleMemberName, AssetsFileInstance FileInst)> assetsFiles)
    {
        _am = am;
        _ownedPrimaryStream = ownedPrimaryStream;
        _ownedUnpackedTempStream = ownedUnpackedTempStream;
        BundleInstance = bundleInst;
        AssetsFiles = assetsFiles;
    }

    public static OpenedAssetsContainer Open(
        AssetsManager am,
        string fullPath,
        ClassifiedUnityFileKind kind,
        string? onlyBundleMember = null)
    {
        if (kind == ClassifiedUnityFileKind.SerializedAssetsFile)
        {
            var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            AssetsFileInstance inst = am.LoadAssetsFile(fs, fullPath, loadDeps: false);
            return new OpenedAssetsContainer(am, fs, null, null, [(null, inst)]);
        }

        // AssetBundle: open via FileStream and unpack large compressed bundles to temp disk stream instead of RAM
        var bundleFs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long bundleLength = bundleFs.Length;
        const long largeBundleThreshold = 32L * 1024 * 1024; // 32 MB

        Stream? tempUnpackedStream = null;
        BundleFileInstance bundleInst;

        if (bundleLength <= largeBundleThreshold)
        {
            bundleInst = am.LoadBundleFile(bundleFs, fullPath, unpackIfPacked: true);
        }
        else
        {
            bundleInst = am.LoadBundleFile(bundleFs, fullPath, unpackIfPacked: false);
            if (bundleInst.file.DataIsCompressed)
            {
                string tempFile = Path.Combine(Path.GetTempPath(), $"translator_bundle_{Guid.NewGuid():N}.tmp");
                tempUnpackedStream = new FileStream(
                    tempFile,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 65536,
                    FileOptions.DeleteOnClose);

                var writer = new AssetsFileWriter(tempUnpackedStream);
                bundleInst.file.Unpack(writer);
                tempUnpackedStream.Position = 0;

                var unpackedBundle = new AssetBundleFile();
                unpackedBundle.Read(new AssetsFileReader(tempUnpackedStream));
                bundleInst.file = unpackedBundle;
            }
        }

        var loadedFiles = new List<(string? BundleMemberName, AssetsFileInstance FileInst)>();
        List<string> memberNames = bundleInst.file.GetAllFileNames();

        for (int i = 0; i < memberNames.Count; i++)
        {
            string memberName = memberNames[i];
            if (onlyBundleMember is not null &&
                !string.Equals(memberName, onlyBundleMember, StringComparison.Ordinal))
            {
                continue;
            }

            if (!bundleInst.file.IsAssetsFile(i))
            {
                continue;
            }

            try
            {
                AssetsFileInstance? subInst = am.LoadAssetsFileFromBundle(bundleInst, i, loadDeps: false);
                if (subInst?.file is not null)
                {
                    loadedFiles.Add((memberName, subInst));
                }
            }
            catch
            {
                // Skip unreadable non-serialized sub-entry
            }
        }

        return new OpenedAssetsContainer(am, bundleFs, tempUnpackedStream, bundleInst, loadedFiles);
    }

    public void Dispose()
    {
        try
        {
            _am.UnloadAll(unloadClassData: false);
        }
        catch
        {
        }

        _ownedUnpackedTempStream?.Dispose();
        _ownedPrimaryStream?.Dispose();
    }
}
