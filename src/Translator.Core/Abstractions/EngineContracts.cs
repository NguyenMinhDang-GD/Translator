using Translator.Core.Models;

namespace Translator.Core.Abstractions;

public sealed record ParsedTextNode(
    string InnerKey,
    string Value,
    string Context);

public interface IInnerContentParser
{
    InnerTextFormat Format { get; }

    bool CanParse(string fileNameOrHint, ReadOnlySpan<char> content);

    IReadOnlyList<ParsedTextNode> Parse(string content);
}

public interface IGameEngineAdapter
{
    string EngineName { get; }

    Task<GameProfile?> DetectGameAsync(
        string folderPath,
        CancellationToken cancellationToken = default);

    Task<ScanPhase1Result> DiscoverTargetsAsync(
        GameProfile profile,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ExtractedTargetContent> ExtractTargetAsync(
        GameProfile profile,
        ScannedTarget target,
        CancellationToken cancellationToken = default);
}
