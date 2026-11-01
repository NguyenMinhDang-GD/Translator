using System.Collections.Concurrent;
using AssetsTools.NET.Extra;
using Translator.Core.Abstractions;
using Translator.Core.Models;
using Translator.Core.Parsers;
using Translator.Engines.Unity.Detection;
using Translator.Engines.Unity.Handlers;
using Translator.Engines.Unity.TypeTrees;

namespace Translator.Engines.Unity;

public sealed class UnityEngineAdapter : IGameEngineAdapter, IDisposable
{
    private readonly InnerContentParserRegistry _parserRegistry;
    private readonly ConcurrentDictionary<string, ClassifiedUnityFileKind> _containerKinds = new(StringComparer.OrdinalIgnoreCase);

    private TypeTreeResolver? _activeResolver;
    private string? _activeRootPath;
    private readonly object _resolverLock = new();

    public UnityEngineAdapter(InnerContentParserRegistry? parserRegistry = null)
    {
        _parserRegistry = parserRegistry ?? new InnerContentParserRegistry();
    }

    public string EngineName => "Unity";

    public Task<GameProfile?> DetectGameAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => UnityGameDetector.Detect(folderPath), cancellationToken);
    }

    public Task<ScanPhase1Result> DiscoverTargetsAsync(
        GameProfile profile,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            _containerKinds.Clear();
            TypeTreeResolver resolver = GetOrCreateResolver(profile);

            IReadOnlyList<ClassifiedUnityFile> classifiedFiles =
                MagicByteClassifier.ScanAndClassifyDirectory(profile, cancellationToken);

            var targets = new List<ScannedTarget>();
            var warnings = new List<EncryptedFileWarning>();

            int total = classifiedFiles.Count;
            int done = 0;

            foreach (ClassifiedUnityFile file in classifiedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                progress?.Report(new ScanProgress("Phase1", done, total, file.RelativePath));

                _containerKinds[file.RelativePath] = file.Kind;

                switch (file.Kind)
                {
                    case ClassifiedUnityFileKind.SuspectedEncrypted:
                        warnings.Add(new EncryptedFileWarning(
                            file.RelativePath,
                            "Suspected Encrypted File",
                            file.WarningReason ?? "Header không khớp chuẩn Unity."));
                        break;

                    case ClassifiedUnityFileKind.LooseTextFile:
                        targets.Add(LooseFileHandler.CreateScannedTarget(file));
                        break;

                    case ClassifiedUnityFileKind.ManagedAssemblyDll:
                    case ClassifiedUnityFileKind.Il2CppGlobalMetadata:
                        targets.Add(CodeStringHandler.CreateScannedTarget(file));
                        break;

                    case ClassifiedUnityFileKind.SerializedAssetsFile:
                    case ClassifiedUnityFileKind.AssetBundleFile:
                        DiscoverInsideUnityContainer(file, resolver, targets, warnings, cancellationToken);
                        break;
                }
            }

            // Sort targets: non-noise and highest relevance score at top
            List<ScannedTarget> sorted = targets
                .OrderBy(t => t.IsLikelyNoise ? 1 : 0)
                .ThenByDescending(t => t.RelevanceScore)
                .ThenBy(t => t.SourceKind)
                .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ScanPhase1Result(profile, sorted, warnings);
        }, cancellationToken);
    }

    public async Task<ExtractedTargetContent> ExtractTargetAsync(
        GameProfile profile,
        ScannedTarget target,
        CancellationToken cancellationToken = default)
    {
        ClassifiedUnityFileKind kind = ResolveContainerKind(profile, target);

        switch (target.SourceKind)
        {
            case TextSourceKind.LooseFile:
                return await LooseFileHandler
                    .ExtractAsync(profile, target, _parserRegistry, cancellationToken)
                    .ConfigureAwait(false);

            case TextSourceKind.CodeString:
                return await CodeStringHandler
                    .ExtractAsync(profile, target, kind, cancellationToken)
                    .ConfigureAwait(false);

            case TextSourceKind.TextAsset:
                return await Task.Run(() =>
                {
                    TypeTreeResolver resolver = GetOrCreateResolver(profile);
                    AssetsManager am = resolver.CreateAssetsManager();
                    return TextAssetHandler.Extract(profile, target, am, kind, _parserRegistry);
                }, cancellationToken).ConfigureAwait(false);

            case TextSourceKind.MonoBehaviour:
                return await Task.Run(() =>
                {
                    TypeTreeResolver resolver = GetOrCreateResolver(profile);
                    AssetsManager am = resolver.CreateAssetsManager();
                    return MonoBehaviourHandler.Extract(profile, target, am, kind, resolver, cancellationToken);
                }, cancellationToken).ConfigureAwait(false);

            default:
                throw new NotSupportedException($"Không hỗ trợ loại nguồn {target.SourceKind}.");
        }
    }

    private static void DiscoverInsideUnityContainer(
        ClassifiedUnityFile containerFile,
        TypeTreeResolver resolver,
        List<ScannedTarget> targets,
        List<EncryptedFileWarning> warnings,
        CancellationToken cancellationToken)
    {
        AssetsManager am = resolver.CreateAssetsManager();
        try
        {
            using var opened = OpenedAssetsContainer.Open(am, containerFile.FullPath, containerFile.Kind);
            foreach ((string? memberName, AssetsFileInstance fileInst) in opened.AssetsFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    am.LoadDependencies(fileInst);
                }
                catch
                {
                    // Optional external dependencies may not all exist
                }

                TextAssetHandler.DiscoverTargets(
                    fileInst,
                    containerFile.RelativePath,
                    memberName,
                    targets,
                    cancellationToken);

                MonoBehaviourHandler.DiscoverTargets(
                    am,
                    fileInst,
                    containerFile.RelativePath,
                    memberName,
                    resolver,
                    targets,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string category = containerFile.Kind == ClassifiedUnityFileKind.AssetBundleFile
                ? "AssetBundle (Unpack Error / Encrypted Blocks)"
                : "Serialized AssetsFile";

            warnings.Add(new EncryptedFileWarning(
                containerFile.RelativePath,
                category,
                $"Không thể giải nén/đọc cấu trúc bên trong (nghi bị mã hóa block LZ4/AES hoặc header tùy biến): {ex.Message}"));
        }
    }

    private ClassifiedUnityFileKind ResolveContainerKind(GameProfile profile, ScannedTarget target)
    {
        if (_containerKinds.TryGetValue(target.ContainerRelativePath, out ClassifiedUnityFileKind cached))
        {
            return cached;
        }

        string fullPath = Path.Combine(profile.RootPath, target.ContainerRelativePath);
        ClassifiedUnityFile classified = MagicByteClassifier.ClassifySingleFile(fullPath, profile);
        _containerKinds[target.ContainerRelativePath] = classified.Kind;
        return classified.Kind;
    }

    private TypeTreeResolver GetOrCreateResolver(GameProfile profile)
    {
        lock (_resolverLock)
        {
            if (_activeResolver is not null &&
                string.Equals(_activeRootPath, profile.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                return _activeResolver;
            }

            _activeResolver?.Dispose();
            _activeResolver = new TypeTreeResolver(profile);
            _activeRootPath = profile.RootPath;
            return _activeResolver;
        }
    }

    public void Dispose()
    {
        lock (_resolverLock)
        {
            _activeResolver?.Dispose();
            _activeResolver = null;
        }
    }
}
