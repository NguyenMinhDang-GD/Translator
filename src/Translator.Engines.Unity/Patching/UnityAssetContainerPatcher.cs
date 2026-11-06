using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Translator.Core.Filters;
using Translator.Core.Models;
using Translator.Core.Parsers;
using Translator.Engines.Unity.Detection;
using Translator.Engines.Unity.TypeTrees;

namespace Translator.Engines.Unity.Patching;

public static class UnityAssetContainerPatcher
{
    public static int PatchSerializedAssetsFileAtomic(
        GameProfile profile,
        string backupFilePath,
        string targetGameFilePath,
        IReadOnlyList<PatchPayload> payloads,
        TypeTreeResolver typeTreeResolver,
        InnerContentParserRegistry parserRegistry,
        CancellationToken cancellationToken = default)
    {
        AssetsManager am = typeTreeResolver.CreateAssetsManager();
        int totalPatchedEntries = 0;
        string tempPath = $"{targetGameFilePath}.translator.tmp";

        try
        {
            using var backupFs = new FileStream(backupFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            AssetsFileInstance fileInst = am.LoadAssetsFile(backupFs, backupFilePath, loadDeps: false);

            try
            {
                am.LoadDependencies(fileInst);
            }
            catch
            {
            }

            totalPatchedEntries = ApplyPayloadsToAssetsFileInstance(
                am,
                fileInst,
                payloads,
                typeTreeResolver,
                parserRegistry,
                cancellationToken);

            if (totalPatchedEntries == 0)
            {
                return 0;
            }

            using (var outFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                var writer = new AssetsFileWriter(outFs);
                fileInst.file.Write(writer, 0);
                outFs.Flush();
            }
        }
        finally
        {
            try
            {
                am.UnloadAll(unloadClassData: false);
            }
            catch
            {
            }
        }

        File.Move(tempPath, targetGameFilePath, overwrite: true);
        return totalPatchedEntries;
    }

    public static int PatchAssetBundleFileAtomic(
        GameProfile profile,
        string backupBundlePath,
        string targetGameBundlePath,
        IReadOnlyList<PatchPayload> payloads,
        TypeTreeResolver typeTreeResolver,
        InnerContentParserRegistry parserRegistry,
        CancellationToken cancellationToken = default)
    {
        AssetsManager am = typeTreeResolver.CreateAssetsManager();
        int totalPatchedEntries = 0;
        string tempPath = $"{targetGameBundlePath}.translator.tmp";

        try
        {
            using var backupFs = new FileStream(backupBundlePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BundleFileInstance bundleInst = am.LoadBundleFile(backupFs, backupBundlePath, unpackIfPacked: false);

            using var unpackedMem = new MemoryStream();
            if (bundleInst.file.DataIsCompressed)
            {
                var unpackWriter = new AssetsFileWriter(unpackedMem);
                bundleInst.file.Unpack(unpackWriter);
                unpackedMem.Position = 0;

                var unpackedBundle = new AssetBundleFile();
                unpackedBundle.Read(new AssetsFileReader(unpackedMem));
                bundleInst.file = unpackedBundle;
            }

            List<string> memberNames = bundleInst.file.GetAllFileNames();
            bool anyMemberModified = false;

            for (int i = 0; i < memberNames.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string memberName = memberNames[i];
                if (!bundleInst.file.IsAssetsFile(i))
                {
                    continue;
                }

                List<PatchPayload> matchingPayloads = payloads
                    .Where(p =>
                        string.IsNullOrEmpty(p.Package.BundleMemberName) ||
                        string.Equals(p.Package.BundleMemberName, memberName, StringComparison.OrdinalIgnoreCase) ||
                        p.Package.Entries.Any(e =>
                            string.IsNullOrEmpty(e.Location.BundleMemberName) ||
                            string.Equals(e.Location.BundleMemberName, memberName, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (matchingPayloads.Count == 0)
                {
                    continue;
                }

                AssetsFileInstance? subInst = am.LoadAssetsFileFromBundle(bundleInst, i, loadDeps: false);
                if (subInst?.file is null)
                {
                    continue;
                }

                int memberPatched = ApplyPayloadsToAssetsFileInstance(
                    am,
                    subInst,
                    matchingPayloads,
                    typeTreeResolver,
                    parserRegistry,
                    cancellationToken);

                if (memberPatched > 0)
                {
                    bundleInst.file.BlockAndDirInfo.DirectoryInfos[i].SetNewData(subInst.file);
                    totalPatchedEntries += memberPatched;
                    anyMemberModified = true;
                }
            }

            if (!anyMemberModified || totalPatchedEntries == 0)
            {
                return 0;
            }

            // Write modified unpacked bundle to intermediate stream, then pack with LZ4 compression
            using var modifiedUnpackedStream = new MemoryStream();
            var bundleWriter = new AssetsFileWriter(modifiedUnpackedStream);
            bundleInst.file.Write(bundleWriter, 0);
            modifiedUnpackedStream.Position = 0;

            var bundleToPack = new AssetBundleFile();
            bundleToPack.Read(new AssetsFileReader(modifiedUnpackedStream));

            using (var outFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                var packWriter = new AssetsFileWriter(outFs);
                bundleToPack.Pack(packWriter, AssetBundleCompressionType.LZ4);
                outFs.Flush();
            }

            bundleToPack.Close();
        }
        finally
        {
            try
            {
                am.UnloadAll(unloadClassData: false);
            }
            catch
            {
            }
        }

        File.Move(tempPath, targetGameBundlePath, overwrite: true);
        return totalPatchedEntries;
    }

    private static int ApplyPayloadsToAssetsFileInstance(
        AssetsManager am,
        AssetsFileInstance fileInst,
        IReadOnlyList<PatchPayload> payloads,
        TypeTreeResolver typeTreeResolver,
        InnerContentParserRegistry parserRegistry,
        CancellationToken cancellationToken)
    {
        int patchedCount = 0;

        foreach (PatchPayload payload in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (payload.Package.SourceKind)
            {
                case TextSourceKind.TextAsset:
                    patchedCount += PatchTextAssetPayload(am, fileInst, payload, parserRegistry);
                    break;

                case TextSourceKind.MonoBehaviour:
                    patchedCount += PatchMonoBehaviourPayload(am, fileInst, payload, typeTreeResolver);
                    break;
            }
        }

        return patchedCount;
    }

    private static int PatchTextAssetPayload(
        AssetsManager am,
        AssetsFileInstance fileInst,
        PatchPayload payload,
        InnerContentParserRegistry parserRegistry)
    {
        long? pathId = payload.Package.Entries.FirstOrDefault(e => e.Location.PathId.HasValue)?.Location.PathId;
        if (!pathId.HasValue)
        {
            pathId = TryExtractTrailingPathIdFromTargetId(payload.Package.TargetId);
        }

        if (!pathId.HasValue)
        {
            return 0;
        }

        AssetFileInfo? info = fileInst.file.GetAssetInfo(pathId.Value);
        if (info is null)
        {
            return 0;
        }

        byte[] rawObjectBytes = TypeTreeResolver.ReadRawAssetBytes(fileInst, info);
        bool isBigEndian = fileInst.file.Header.Endianness;

        if (!AlignedBinaryStringScanner.TryReadBinaryTextAsset(rawObjectBytes, isBigEndian, out string assetName, out byte[] oldScriptBytes))
        {
            assetName = payload.Package.DisplayName;
            oldScriptBytes = [];
        }

        int nameByteLen = Encoding.UTF8.GetByteCount(assetName);
        int consumedLen = Align4(4 + nameByteLen) + Align4(4 + oldScriptBytes.Length);
        ReadOnlySpan<byte> trailingBytes = consumedLen < rawObjectBytes.Length
            ? rawObjectBytes.AsSpan(consumedLen)
            : ReadOnlySpan<byte>.Empty;

        byte[] newScriptBytes;
        int appliedEntries = 0;

        if (payload.IsRawOverride)
        {
            if (payload.RawBytesOverride is not null)
            {
                newScriptBytes = payload.RawBytesOverride;
                appliedEntries = Math.Max(1, payload.Package.Entries.Count);
            }
            else if (payload.RawTextOverride is not null)
            {
                newScriptBytes = EncodeTextMatchingOriginalBom(payload.RawTextOverride, oldScriptBytes);
                appliedEntries = Math.Max(1, payload.Package.Entries.Count);
            }
            else
            {
                return 0;
            }
        }
        else
        {
            if (!TextHeuristicScorer.TryDecodeReadableText(oldScriptBytes, out string originalText))
            {
                return 0;
            }

            var translations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (TextEntry entry in payload.Package.Entries)
            {
                if (string.IsNullOrEmpty(entry.TranslatedText))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.Location.FieldOrInnerKey))
                {
                    translations[entry.Location.FieldOrInnerKey] = entry.TranslatedText;
                }

                if (!string.IsNullOrWhiteSpace(entry.Key))
                {
                    translations[entry.Key] = entry.TranslatedText;
                }

                appliedEntries++;
            }

            if (translations.Count == 0)
            {
                return 0;
            }

            string reconstructedText = parserRegistry.Reconstruct(payload.Package.InnerFormat, originalText, translations);
            if (string.Equals(reconstructedText, originalText, StringComparison.Ordinal))
            {
                return 0;
            }

            newScriptBytes = EncodeTextMatchingOriginalBom(reconstructedText, oldScriptBytes);
        }

        byte[] newObjectBytes = AlignedBinaryStringScanner.BuildBinaryTextAsset(
            assetName,
            newScriptBytes,
            isBigEndian,
            trailingBytes);

        info.SetNewData(newObjectBytes);
        return appliedEntries;
    }

    private static int PatchMonoBehaviourPayload(
        AssetsManager am,
        AssetsFileInstance fileInst,
        PatchPayload payload,
        TypeTreeResolver typeTreeResolver)
    {
        var byPathId = new Dictionary<long, Dictionary<string, string>>();

        foreach (TextEntry entry in payload.Package.Entries)
        {
            if (string.IsNullOrEmpty(entry.TranslatedText))
            {
                continue;
            }

            long? pathId = entry.Location.PathId;
            string fieldKey = entry.Location.FieldOrInnerKey;

            if (!pathId.HasValue && entry.Key.StartsWith("PathId=", StringComparison.OrdinalIgnoreCase))
            {
                int sep = entry.Key.IndexOf("::", StringComparison.Ordinal);
                if (sep > 7 && long.TryParse(entry.Key.AsSpan(7, sep - 7), out long parsedId))
                {
                    pathId = parsedId;
                    fieldKey = entry.Key[(sep + 2)..];
                }
            }

            if (!pathId.HasValue)
            {
                continue;
            }

            if (!byPathId.TryGetValue(pathId.Value, out Dictionary<string, string>? dict))
            {
                dict = new Dictionary<string, string>(StringComparer.Ordinal);
                byPathId[pathId.Value] = dict;
            }

            dict[fieldKey] = entry.TranslatedText;
            dict[entry.Key] = entry.TranslatedText;
        }

        if (byPathId.Count == 0)
        {
            return 0;
        }

        int totalApplied = 0;
        bool isBigEndian = fileInst.file.Header.Endianness;

        foreach ((long pathId, Dictionary<string, string> fieldTranslations) in byPathId)
        {
            AssetFileInfo? info = fileInst.file.GetAssetInfo(pathId);
            if (info is null)
            {
                continue;
            }

            ScriptIdentity scriptId = typeTreeResolver.ResolveMonoBehaviourScript(am, fileInst, info);
            AssetTypeValueField? baseField = typeTreeResolver.TryDeserializeMonoBehaviour(am, fileInst, info, scriptId);

            if (baseField is not null && !baseField.IsDummy)
            {
                int objModified = 0;
                foreach (AssetTypeValueField child in baseField.Children)
                {
                    string fieldName = child.TemplateField.Name;
                    if (fieldName is "m_GameObject" or "m_Enabled" or "m_Script" or "m_Name")
                    {
                        continue;
                    }

                    objModified += ApplyTranslationsToValueField(child, fieldName, pathId, fieldTranslations, depth: 0);
                }

                if (objModified > 0)
                {
                    byte[] newBytes = baseField.WriteToByteArray(isBigEndian);
                    info.SetNewData(newBytes);
                    totalApplied += objModified;
                    continue;
                }
            }

            // Fallback: Aligned Binary String Replacement
            byte[] rawBytes = TypeTreeResolver.ReadRawAssetBytes(fileInst, info);
            byte[]? patchedBinary = AlignedBinaryStringScanner.PatchBinaryMonoBehaviourPayload(
                rawBytes,
                pathId,
                fieldTranslations,
                isBigEndian,
                out int binPatched);

            if (patchedBinary is not null && binPatched > 0)
            {
                info.SetNewData(patchedBinary);
                totalApplied += binPatched;
            }
        }

        return totalApplied;
    }

    private static int ApplyTranslationsToValueField(
        AssetTypeValueField field,
        string currentPath,
        long pathId,
        IReadOnlyDictionary<string, string> translations,
        int depth)
    {
        if (field.IsDummy || depth > 24)
        {
            return 0;
        }

        AssetValueType valType = field.TemplateField.ValueType;

        if (valType == AssetValueType.String)
        {
            if (translations.TryGetValue(currentPath, out string? translated) && !string.IsNullOrEmpty(translated))
            {
                field.AsString = translated;
                return 1;
            }

            string fullKey = $"PathId={pathId}::{currentPath}";
            if (translations.TryGetValue(fullKey, out string? translatedFull) && !string.IsNullOrEmpty(translatedFull))
            {
                field.AsString = translatedFull;
                return 1;
            }

            return 0;
        }

        if (valType == AssetValueType.ByteArray)
        {
            return 0;
        }

        int modified = 0;

        if (field.TemplateField.IsArray || valType == AssetValueType.Array)
        {
            List<AssetTypeValueField> items = field.Children;
            for (int i = 0; i < items.Count; i++)
            {
                modified += ApplyTranslationsToValueField(
                    items[i],
                    $"{currentPath}[{i}]",
                    pathId,
                    translations,
                    depth + 1);
            }

            return modified;
        }

        if (field.Children is { Count: > 0 })
        {
            foreach (AssetTypeValueField child in field.Children)
            {
                string cName = child.TemplateField.Name;
                if (cName is "m_FileID" or "m_PathID")
                {
                    continue;
                }

                string nextPath = cName == "Array" ? currentPath : $"{currentPath}.{cName}";
                modified += ApplyTranslationsToValueField(
                    child,
                    nextPath,
                    pathId,
                    translations,
                    depth + 1);
            }
        }

        return modified;
    }

    private static byte[] EncodeTextMatchingOriginalBom(string text, ReadOnlySpan<byte> originalBytes)
    {
        if (originalBytes.Length >= 3 &&
            originalBytes[0] == 0xEF &&
            originalBytes[1] == 0xBB &&
            originalBytes[2] == 0xBF)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            byte[] withBom = new byte[utf8.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            utf8.CopyTo(withBom.AsSpan(3));
            return withBom;
        }

        if (originalBytes.Length >= 2 && originalBytes[0] == 0xFF && originalBytes[1] == 0xFE)
        {
            byte[] utf16Le = Encoding.Unicode.GetBytes(text);
            byte[] withBom = new byte[utf16Le.Length + 2];
            withBom[0] = 0xFF;
            withBom[1] = 0xFE;
            utf16Le.CopyTo(withBom.AsSpan(2));
            return withBom;
        }

        if (originalBytes.Length >= 2 && originalBytes[0] == 0xFE && originalBytes[1] == 0xFF)
        {
            byte[] utf16Be = Encoding.BigEndianUnicode.GetBytes(text);
            byte[] withBom = new byte[utf16Be.Length + 2];
            withBom[0] = 0xFE;
            withBom[1] = 0xFF;
            utf16Be.CopyTo(withBom.AsSpan(2));
            return withBom;
        }

        return Encoding.UTF8.GetBytes(text);
    }

    private static long? TryExtractTrailingPathIdFromTargetId(string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return null;
        }

        int lastSep = targetId.LastIndexOf("::", StringComparison.Ordinal);
        if (lastSep >= 0 && long.TryParse(targetId.AsSpan(lastSep + 2), out long parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
