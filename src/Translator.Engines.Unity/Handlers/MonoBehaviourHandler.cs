using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Translator.Core.Exporting;
using Translator.Core.Filters;
using Translator.Core.Models;
using Translator.Engines.Unity.Detection;
using Translator.Engines.Unity.TypeTrees;

namespace Translator.Engines.Unity.Handlers;

internal static class MonoBehaviourHandler
{
    private const int MonoBehaviourClassId = 114;

    private static readonly HashSet<string> KnownNonTextUiClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Image", "RawImage", "Button", "Toggle", "Slider", "Scrollbar", "ScrollRect",
        "CanvasScaler", "GraphicRaycaster", "ContentSizeFitter", "VerticalLayoutGroup",
        "HorizontalLayoutGroup", "GridLayoutGroup", "LayoutElement", "Mask", "RectMask2D",
        "EventSystem", "StandaloneInputModule", "InputSystemUIInputModule",
        "Outline", "Shadow", "AspectRatioFitter", "Selectable",
        "TMP_FontAsset", "TMP_SpriteAsset", "TMP_StyleSheet", "TMP_Settings",
        "TMP_SubMeshUI", "TMP_SelectionCaret", "UniversalAdditionalCameraData",
        "UniversalAdditionalLightData", "Volume", "PostProcessVolume", "PostProcessLayer"
    };

    private static readonly HashSet<string> HighPriorityTextClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "LanguageSourceAsset", "LanguageSource", "StringTable", "SharedTableData",
        "TextMeshProUGUI", "TextMeshPro", "Text"
    };

    public static void DiscoverTargets(
        AssetsManager am,
        AssetsFileInstance fileInst,
        string containerRelativePath,
        string? bundleMemberName,
        TypeTreeResolver typeTreeResolver,
        List<ScannedTarget> output,
        CancellationToken cancellationToken)
    {
        AssetsFile file = fileInst.file;
        List<AssetFileInfo> monoBehaviours = file.GetAssetsOfType(MonoBehaviourClassId);
        if (monoBehaviours.Count == 0)
        {
            return;
        }

        var grouped = new Dictionary<string, (ScriptIdentity Script, List<AssetFileInfo> Infos)>(StringComparer.Ordinal);

        foreach (AssetFileInfo info in monoBehaviours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (info.ByteSize < 28)
            {
                continue;
            }

            ScriptIdentity script = typeTreeResolver.ResolveMonoBehaviourScript(am, fileInst, info);
            string groupKey = string.IsNullOrEmpty(script.Namespace)
                ? script.ClassName
                : $"{script.Namespace}.{script.ClassName}";

            if (!grouped.TryGetValue(groupKey, out var bucket))
            {
                bucket = (script, []);
                grouped[groupKey] = bucket;
            }

            bucket.Infos.Add(info);
        }

        foreach ((string fullClassName, (ScriptIdentity script, List<AssetFileInfo> infos)) in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long totalBytes = 0;
            var pathIds = new List<long>(infos.Count);
            foreach (AssetFileInfo i in infos)
            {
                totalBytes += i.ByteSize;
                pathIds.Add(i.PathId);
            }

            string displayName = infos.Count == 1
                ? $"{script.ClassName} (PathID {infos[0].PathId})"
                : $"{script.ClassName} [{infos.Count} objects]";

            (int score, bool isNoise) = TextHeuristicScorer.ScoreTargetMetadata(
                displayName,
                scriptClassName: fullClassName,
                relativePath: containerRelativePath);

            if (KnownNonTextUiClasses.Contains(script.ClassName))
            {
                score = 8;
                isNoise = true;
            }
            else if (HighPriorityTextClasses.Contains(script.ClassName))
            {
                score = Math.Max(score, script.ClassName is "Text" or "TextMeshProUGUI" or "TextMeshPro" ? 72 : 92);
                isNoise = false;
            }

            output.Add(new ScannedTarget
            {
                TargetId = $"MonoBehaviour::{containerRelativePath}::{bundleMemberName ?? "-"}::{fullClassName}",
                DisplayName = displayName,
                SourceKind = TextSourceKind.MonoBehaviour,
                ContainerRelativePath = containerRelativePath,
                BundleMemberName = bundleMemberName,
                PrimaryPathId = infos[0].PathId,
                ScriptClassName = fullClassName,
                EstimatedSizeBytes = totalBytes,
                ObjectCount = infos.Count,
                RelevanceScore = score,
                IsLikelyNoise = isNoise,
                SupportsRawExport = false,
                GroupedPathIds = pathIds
            });
        }
    }

    public static ExtractedTargetContent Extract(
        GameProfile profile,
        ScannedTarget target,
        AssetsManager am,
        ClassifiedUnityFileKind containerKind,
        TypeTreeResolver typeTreeResolver,
        CancellationToken cancellationToken)
    {
        string fullContainerPath = Path.Combine(profile.RootPath, target.ContainerRelativePath);
        string sha256 = HashUtility.ComputeFileSha256(fullContainerPath);

        using var opened = OpenedAssetsContainer.Open(am, fullContainerPath, containerKind, target.BundleMemberName);
        AssetsFileInstance? fileInst = opened.AssetsFiles.FirstOrDefault().FileInst;
        if (fileInst is null)
        {
            return new ExtractedTargetContent(
                target,
                RawText: null,
                RawBytes: null,
                RawExtension: null,
                ContainerSha256: sha256,
                InnerFormat: InnerTextFormat.StructuredFields,
                Entries: []);
        }

        // Load external dependencies lazily only if MonoScript lives in another file
        try
        {
            am.LoadDependencies(fileInst);
        }
        catch
        {
            // Safe to proceed even if some optional external sharedassets are absent
        }

        IReadOnlyList<long> pathIds = target.GroupedPathIds
            ?? (target.PrimaryPathId.HasValue ? [target.PrimaryPathId.Value] : []);

        var entries = new List<TextEntry>();
        int entryCounter = 1;
        bool isBigEndian = fileInst.file.Header.Endianness;
        bool useLongPathId = fileInst.file.Header.Version >= 14;

        foreach (long pathId in pathIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssetFileInfo? info = fileInst.file.GetAssetInfo(pathId);
            if (info is null)
            {
                continue;
            }

            ScriptIdentity scriptId = typeTreeResolver.ResolveMonoBehaviourScript(am, fileInst, info);
            AssetTypeValueField? baseField = typeTreeResolver.TryDeserializeMonoBehaviour(am, fileInst, info, scriptId);

            if (baseField is not null && !baseField.IsDummy)
            {
                string objName = baseField["m_Name"].IsDummy ? string.Empty : baseField["m_Name"].AsString;
                string objLabel = string.IsNullOrWhiteSpace(objName)
                    ? $"{scriptId.ClassName}#{pathId}"
                    : $"{scriptId.ClassName} '{objName}' (#{pathId})";

                foreach (AssetTypeValueField child in baseField.Children)
                {
                    string fieldName = child.TemplateField.Name;
                    if (fieldName is "m_GameObject" or "m_Enabled" or "m_Script" or "m_Name")
                    {
                        continue;
                    }

                    WalkValueField(
                        child,
                        currentPath: fieldName,
                        objectContext: objLabel,
                        siblingHint: null,
                        target,
                        pathId,
                        scriptId.ClassName,
                        entries,
                        ref entryCounter);
                }
            }
            else
            {
                // Level-3 Fallback: Aligned Binary String Scanner
                byte[] rawBytes = TypeTreeResolver.ReadRawAssetBytes(fileInst, info);
                (string objName, IReadOnlyList<BinaryScannedString> scannedStrings) =
                    AlignedBinaryStringScanner.ScanMonoBehaviourPayload(rawBytes, isBigEndian, useLongPathId);

                string objLabel = string.IsNullOrWhiteSpace(objName)
                    ? $"[BinaryFallback] {scriptId.ClassName}#{pathId}"
                    : $"[BinaryFallback] {scriptId.ClassName} '{objName}' (#{pathId})";

                foreach (BinaryScannedString str in scannedStrings)
                {
                    string key = $"PathId={pathId}::bin:offset={str.RelativeByteOffset};len={str.ByteLength}";
                    (int score, bool isNoise) = TextHeuristicScorer.EvaluateString(str.Value);

                    entries.Add(new TextEntry
                    {
                        Id = $"entry_{entryCounter++:D4}",
                        Key = key,
                        Context = $"{objLabel} @ +0x{str.RelativeByteOffset:X}",
                        OriginalText = str.Value,
                        RelevanceScore = score,
                        IsSystemNoise = isNoise,
                        Location = new EntryLocation
                        {
                            ContainerRelativePath = target.ContainerRelativePath,
                            BundleMemberName = target.BundleMemberName,
                            AssetClassId = MonoBehaviourClassId,
                            PathId = pathId,
                            ScriptClassName = target.ScriptClassName,
                            FieldOrInnerKey = key
                        }
                    });
                }
            }
        }

        return new ExtractedTargetContent(
            target,
            RawText: null,
            RawBytes: null,
            RawExtension: null,
            ContainerSha256: sha256,
            InnerFormat: InnerTextFormat.StructuredFields,
            Entries: entries);
    }

    private static void WalkValueField(
        AssetTypeValueField field,
        string currentPath,
        string objectContext,
        string? siblingHint,
        ScannedTarget target,
        long pathId,
        string className,
        List<TextEntry> entries,
        ref int entryCounter,
        int depth = 0)
    {
        if (field.IsDummy || depth > 24)
        {
            return;
        }

        AssetValueType valType = field.TemplateField.ValueType;

        if (valType == AssetValueType.String)
        {
            string str = field.AsString;
            if (!string.IsNullOrWhiteSpace(str))
            {
                string leafName = field.TemplateField.Name;
                (int score, bool isNoise) = TextHeuristicScorer.EvaluateString(str, leafName);

                // In I2 Localization, "Term" is the lookup key whereas "Languages" holds the translatable strings
                if (leafName.Equals("Term", StringComparison.Ordinal) && !str.Contains(' '))
                {
                    isNoise = true;
                    score = 15;
                }

                string fullKey = $"PathId={pathId}::{currentPath}";
                string context = string.IsNullOrWhiteSpace(siblingHint)
                    ? $"{objectContext} | {currentPath}"
                    : $"{objectContext} ({siblingHint}) | {currentPath}";

                entries.Add(new TextEntry
                {
                    Id = $"entry_{entryCounter++:D4}",
                    Key = fullKey,
                    Context = context,
                    OriginalText = str,
                    RelevanceScore = score,
                    IsSystemNoise = isNoise,
                    Location = new EntryLocation
                    {
                        ContainerRelativePath = target.ContainerRelativePath,
                        BundleMemberName = target.BundleMemberName,
                        AssetClassId = MonoBehaviourClassId,
                        PathId = pathId,
                        ScriptClassName = target.ScriptClassName ?? className,
                        FieldOrInnerKey = currentPath
                    }
                });
            }
            return;
        }

        if (valType == AssetValueType.ByteArray)
        {
            return;
        }

        if (field.TemplateField.IsArray || valType == AssetValueType.Array)
        {
            List<AssetTypeValueField> items = field.Children;
            for (int i = 0; i < items.Count; i++)
            {
                WalkValueField(
                    items[i],
                    $"{currentPath}[{i}]",
                    objectContext,
                    siblingHint,
                    target,
                    pathId,
                    className,
                    entries,
                    ref entryCounter,
                    depth + 1);
            }
            return;
        }

        if (field.Children is { Count: > 0 })
        {
            // Look for an ID/Term/Key sibling field to enrich translator context (e.g. I2 TermData.Term or Unity StringTableEntry.m_Id)
            string? localHint = siblingHint;
            foreach (AssetTypeValueField child in field.Children)
            {
                string cName = child.TemplateField.Name;
                if (child.TemplateField.ValueType == AssetValueType.String &&
                    cName is "Term" or "m_Key" or "key" or "id" or "m_Id" or "Speaker" or "speaker" or "characterName")
                {
                    string hVal = child.AsString;
                    if (!string.IsNullOrWhiteSpace(hVal) && hVal.Length <= 80)
                    {
                        localHint = $"{cName}={hVal}";
                        break;
                    }
                }
            }

            foreach (AssetTypeValueField child in field.Children)
            {
                string cName = child.TemplateField.Name;
                // Skip Unity PPtr subfields
                if (cName is "m_FileID" or "m_PathID")
                {
                    continue;
                }

                string nextPath = cName == "Array" ? currentPath : $"{currentPath}.{cName}";
                WalkValueField(
                    child,
                    nextPath,
                    objectContext,
                    localHint,
                    target,
                    pathId,
                    className,
                    entries,
                    ref entryCounter,
                    depth + 1);
            }
        }
    }
}
