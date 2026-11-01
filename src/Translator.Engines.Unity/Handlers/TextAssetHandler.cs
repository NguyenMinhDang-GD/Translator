using System.Buffers.Binary;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Translator.Core.Abstractions;
using Translator.Core.Exporting;
using Translator.Core.Filters;
using Translator.Core.Models;
using Translator.Core.Parsers;
using Translator.Engines.Unity.Detection;
using Translator.Engines.Unity.TypeTrees;

namespace Translator.Engines.Unity.Handlers;

internal static class TextAssetHandler
{
    private const int TextAssetClassId = 49;

    public static void DiscoverTargets(
        AssetsFileInstance fileInst,
        string containerRelativePath,
        string? bundleMemberName,
        List<ScannedTarget> output,
        CancellationToken cancellationToken)
    {
        AssetsFile file = fileInst.file;
        bool isBigEndian = file.Header.Endianness;
        List<AssetFileInfo> textAssets = file.GetAssetsOfType(TextAssetClassId);

        foreach (AssetFileInfo info in textAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (info.ByteSize < 8)
            {
                continue;
            }

            if (!TryPeekTextAssetHeader(fileInst, info, isBigEndian, out string assetName, out bool isReadableText))
            {
                continue;
            }

            // Skip raw binary .bytes blobs that cannot be decoded as UTF-8/UTF-16 text
            if (!isReadableText)
            {
                continue;
            }

            string displayName = string.IsNullOrWhiteSpace(assetName)
                ? $"TextAsset_{info.PathId}"
                : assetName;

            (int score, bool isNoise) = TextHeuristicScorer.ScoreTargetMetadata(
                displayName,
                scriptClassName: "TextAsset",
                relativePath: containerRelativePath);

            output.Add(new ScannedTarget
            {
                TargetId = $"TextAsset::{containerRelativePath}::{bundleMemberName ?? "-"}::{info.PathId}",
                DisplayName = displayName,
                SourceKind = TextSourceKind.TextAsset,
                ContainerRelativePath = containerRelativePath,
                BundleMemberName = bundleMemberName,
                PrimaryPathId = info.PathId,
                ScriptClassName = "TextAsset",
                EstimatedSizeBytes = info.ByteSize,
                RelevanceScore = score,
                IsLikelyNoise = isNoise,
                SupportsRawExport = true
            });
        }
    }

    public static ExtractedTargetContent Extract(
        GameProfile profile,
        ScannedTarget target,
        AssetsManager am,
        ClassifiedUnityFileKind containerKind,
        InnerContentParserRegistry parserRegistry)
    {
        string fullContainerPath = Path.Combine(profile.RootPath, target.ContainerRelativePath);
        string sha256 = HashUtility.ComputeFileSha256(fullContainerPath);
        long pathId = target.PrimaryPathId ?? 0;

        using var opened = OpenedAssetsContainer.Open(am, fullContainerPath, containerKind, target.BundleMemberName);
        AssetsFileInstance? fileInst = opened.AssetsFiles.FirstOrDefault().FileInst;
        AssetFileInfo? info = fileInst?.file.GetAssetInfo(pathId);

        if (fileInst is null || info is null)
        {
            return new ExtractedTargetContent(
                target,
                RawText: null,
                RawBytes: null,
                RawExtension: ".txt",
                ContainerSha256: sha256,
                InnerFormat: InnerTextFormat.PlainText,
                Entries: []);
        }

        byte[] rawObjectBytes = TypeTreeResolver.ReadRawAssetBytes(fileInst, info);
        bool isBigEndian = fileInst.file.Header.Endianness;

        byte[] scriptBytes;
        if (!AlignedBinaryStringScanner.TryReadBinaryTextAsset(rawObjectBytes, isBigEndian, out string assetName, out scriptBytes))
        {
            // Fallback via TypeTree if available
            scriptBytes = TryReadViaTypeTree(am, fileInst, info) ?? [];
            assetName = target.DisplayName;
        }

        if (!TextHeuristicScorer.TryDecodeReadableText(scriptBytes, out string decodedText))
        {
            return new ExtractedTargetContent(
                target,
                RawText: null,
                RawBytes: scriptBytes,
                RawExtension: ".bytes",
                ContainerSha256: sha256,
                InnerFormat: InnerTextFormat.PlainText,
                Entries: []);
        }

        string parseHint = string.IsNullOrWhiteSpace(assetName) ? target.DisplayName : assetName;
        (InnerTextFormat format, IReadOnlyList<ParsedTextNode> nodes) =
            parserRegistry.ParseBestEffort(parseHint, decodedText);

        var entries = new List<TextEntry>(nodes.Count);
        int idx = 1;
        foreach (ParsedTextNode node in nodes)
        {
            (int score, bool isNoise) = TextHeuristicScorer.EvaluateString(node.Value, node.InnerKey);
            entries.Add(new TextEntry
            {
                Id = $"entry_{idx++:D4}",
                Key = node.InnerKey,
                Context = $"TextAsset '{target.DisplayName}' (PathID={pathId}) | {node.Context}",
                OriginalText = node.Value,
                RelevanceScore = score,
                IsSystemNoise = isNoise,
                Location = new EntryLocation
                {
                    ContainerRelativePath = target.ContainerRelativePath,
                    BundleMemberName = target.BundleMemberName,
                    AssetClassId = TextAssetClassId,
                    PathId = pathId,
                    FieldOrInnerKey = node.InnerKey
                }
            });
        }

        string ext = GuessExtension(parseHint, format);
        return new ExtractedTargetContent(
            target,
            RawText: decodedText,
            RawBytes: scriptBytes,
            RawExtension: ext,
            ContainerSha256: sha256,
            InnerFormat: format,
            Entries: entries);
    }

    private static bool TryPeekTextAssetHeader(
        AssetsFileInstance fileInst,
        AssetFileInfo info,
        bool isBigEndian,
        out string assetName,
        out bool isReadableText)
    {
        assetName = string.Empty;
        isReadableText = false;

        int sampleSize = (int)Math.Min(info.ByteSize, 6144u);
        long absOffset = info.GetAbsoluteByteOffset(fileInst.file);
        byte[] sample;

        lock (fileInst.LockReader)
        {
            AssetsFileReader reader = fileInst.file.Reader;
            reader.Position = absOffset;
            sample = reader.ReadBytes(sampleSize);
        }

        if (sample.Length < 8)
        {
            return false;
        }

        int nameLen = isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(sample.AsSpan(0, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(sample.AsSpan(0, 4));

        if (nameLen < 0 || nameLen > 1024 || 4 + nameLen > sample.Length)
        {
            return false;
        }

        if (nameLen > 0)
        {
            assetName = Encoding.UTF8.GetString(sample, 4, nameLen);
        }

        int scriptLenOffset = (4 + nameLen + 3) & ~3;
        if (scriptLenOffset + 4 > sample.Length)
        {
            return false;
        }

        int scriptLen = isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(sample.AsSpan(scriptLenOffset, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(sample.AsSpan(scriptLenOffset, 4));

        int scriptStart = scriptLenOffset + 4;
        if (scriptLen <= 0 || scriptStart >= sample.Length)
        {
            return false;
        }

        int availableScriptBytes = Math.Min(scriptLen, sample.Length - scriptStart);
        ReadOnlySpan<byte> scriptSample = sample.AsSpan(scriptStart, availableScriptBytes);

        // Trim incomplete trailing UTF-8 sequence if sample truncated mid-character
        if (availableScriptBytes < scriptLen)
        {
            scriptSample = TrimTrailingPartialUtf8(scriptSample);
        }

        isReadableText = !scriptSample.IsEmpty && TextHeuristicScorer.TryDecodeReadableText(scriptSample, out _);
        return true;
    }

    private static ReadOnlySpan<byte> TrimTrailingPartialUtf8(ReadOnlySpan<byte> span)
    {
        for (int back = 1; back <= 3 && back < span.Length; back++)
        {
            byte b = span[^back];
            if ((b & 0b1000_0000) == 0)
            {
                return span;
            }

            if ((b & 0b1100_0000) == 0b1100_0000)
            {
                int expected = (b & 0b1110_0000) == 0b1100_0000 ? 2
                             : (b & 0b1111_0000) == 0b1110_0000 ? 3 : 4;
                return back < expected ? span[..^back] : span;
            }
        }

        return span;
    }

    private static byte[]? TryReadViaTypeTree(AssetsManager am, AssetsFileInstance fileInst, AssetFileInfo info)
    {
        try
        {
            AssetTypeValueField baseField = am.GetBaseField(fileInst, info);
            AssetTypeValueField scriptField = baseField["m_Script"];
            return scriptField.IsDummy ? null : scriptField.AsByteArray;
        }
        catch
        {
            return null;
        }
    }

    private static string GuessExtension(string assetName, InnerTextFormat format)
    {
        string existingExt = Path.GetExtension(assetName);
        if (!string.IsNullOrWhiteSpace(existingExt) && existingExt.Length <= 6)
        {
            return existingExt;
        }

        return format switch
        {
            InnerTextFormat.Json => ".json",
            InnerTextFormat.Csv => ".csv",
            InnerTextFormat.Tsv => ".tsv",
            InnerTextFormat.Xml => ".xml",
            InnerTextFormat.IniKeyValue => ".ini",
            _ => ".txt"
        };
    }
}
