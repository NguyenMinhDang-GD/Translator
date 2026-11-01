using Translator.Core.Abstractions;
using Translator.Core.Exporting;
using Translator.Core.Filters;
using Translator.Core.Models;
using Translator.Core.Parsers;
using Translator.Engines.Unity.Detection;

namespace Translator.Engines.Unity.Handlers;

internal static class LooseFileHandler
{
    public static ScannedTarget CreateScannedTarget(ClassifiedUnityFile file)
    {
        string fileName = Path.GetFileName(file.FullPath);
        (int score, bool isNoise) = TextHeuristicScorer.ScoreTargetMetadata(
            fileName,
            relativePath: file.RelativePath);

        return new ScannedTarget
        {
            TargetId = $"Loose::{file.RelativePath}",
            DisplayName = fileName,
            SourceKind = TextSourceKind.LooseFile,
            ContainerRelativePath = file.RelativePath,
            EstimatedSizeBytes = file.SizeBytes,
            RelevanceScore = score,
            IsLikelyNoise = isNoise,
            SupportsRawExport = true
        };
    }

    public static async Task<ExtractedTargetContent> ExtractAsync(
        GameProfile profile,
        ScannedTarget target,
        InnerContentParserRegistry parserRegistry,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.Combine(profile.RootPath, target.ContainerRelativePath);
        byte[] rawBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        string sha256 = HashUtility.ComputeFileSha256(fullPath);
        string ext = Path.GetExtension(fullPath);

        if (!TextHeuristicScorer.TryDecodeReadableText(rawBytes, out string decodedText))
        {
            return new ExtractedTargetContent(
                target,
                RawText: null,
                RawBytes: rawBytes,
                RawExtension: ext,
                ContainerSha256: sha256,
                InnerFormat: InnerTextFormat.PlainText,
                Entries: []);
        }

        (InnerTextFormat format, IReadOnlyList<ParsedTextNode> nodes) =
            parserRegistry.ParseBestEffort(target.DisplayName, decodedText);

        var entries = new List<TextEntry>(nodes.Count);
        int index = 1;
        foreach (ParsedTextNode node in nodes)
        {
            (int score, bool isNoise) = TextHeuristicScorer.EvaluateString(node.Value, node.InnerKey);
            entries.Add(new TextEntry
            {
                Id = $"entry_{index++:D4}",
                Key = node.InnerKey,
                Context = $"{target.ContainerRelativePath} | {node.Context}",
                OriginalText = node.Value,
                RelevanceScore = score,
                IsSystemNoise = isNoise,
                Location = new EntryLocation
                {
                    ContainerRelativePath = target.ContainerRelativePath,
                    FieldOrInnerKey = node.InnerKey
                }
            });
        }

        return new ExtractedTargetContent(
            target,
            RawText: decodedText,
            RawBytes: rawBytes,
            RawExtension: string.IsNullOrEmpty(ext) ? ".txt" : ext,
            ContainerSha256: sha256,
            InnerFormat: format,
            Entries: entries);
    }
}
