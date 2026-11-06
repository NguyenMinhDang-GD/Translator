using System.Collections.Concurrent;
using Translator.Core.Abstractions;
using Translator.Core.Filters;
using Translator.Core.Models;

namespace Translator.Core.Indexing;

public sealed class FullTextSearchIndex
{
    private readonly ConcurrentDictionary<string, string> _targetSearchText = new(StringComparer.Ordinal);

    public void IndexTarget(ScannedTarget target, IReadOnlyList<TextEntry> entries, bool includeNoiseInCorpus = true)
    {
        if (entries.Count == 0)
        {
            _targetSearchText[target.TargetId] = $"{target.DisplayName}\n{target.ContainerRelativePath}\n{target.ScriptClassName}";
            return;
        }

        // Cap per-target search corpus at ~512 KB of lowercase/original text so 30GB games stay fast and lean
        const int maxChars = 512 * 1024;
        var sb = new System.Text.StringBuilder(Math.Min(maxChars, entries.Count * 48));
        sb.AppendLine(target.DisplayName);
        sb.AppendLine(target.ContainerRelativePath);
        if (!string.IsNullOrEmpty(target.ScriptClassName))
        {
            sb.AppendLine(target.ScriptClassName);
        }

        foreach (TextEntry entry in entries)
        {
            if (!includeNoiseInCorpus && entry.IsSystemNoise)
            {
                continue;
            }

            if (sb.Length + entry.OriginalText.Length + entry.Key.Length > maxChars)
            {
                break;
            }

            sb.Append(entry.Key).Append('\t').AppendLine(entry.OriginalText);
        }

        _targetSearchText[target.TargetId] = sb.ToString();
    }

    public bool Matches(ScannedTarget target, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        string q = query.Trim();
        if (target.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            target.ContainerRelativePath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (target.ScriptClassName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return true;
        }

        return _targetSearchText.TryGetValue(target.TargetId, out string? corpus) &&
               corpus.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public void Clear() => _targetSearchText.Clear();
}

public sealed class TwoPhaseScanCoordinator
{
    private readonly IReadOnlyList<IGameEngineAdapter> _adapters;
    private readonly ConcurrentDictionary<string, ExtractedTargetContent> _contentCache = new(StringComparer.Ordinal);
    private const int MaxCachedTargets = 128;

    public TwoPhaseScanCoordinator(IEnumerable<IGameEngineAdapter> adapters)
    {
        _adapters = adapters.ToList();
    }

    public FullTextSearchIndex SearchIndex { get; } = new();

    public void InvalidateCache() => _contentCache.Clear();

    public async Task<(IGameEngineAdapter Adapter, GameProfile Profile)?> DetectGameAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        foreach (IGameEngineAdapter adapter in _adapters)
        {
            GameProfile? profile = await adapter.DetectGameAsync(folderPath, cancellationToken).ConfigureAwait(false);
            if (profile is not null)
            {
                return (adapter, profile);
            }
        }

        return null;
    }

    public async Task<ScanPhase1Result> RunPhase1DiscoveryAsync(
        IGameEngineAdapter adapter,
        GameProfile profile,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _contentCache.Clear();
        SearchIndex.Clear();
        return await adapter.DiscoverTargetsAsync(profile, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunPhase2DeepIndexingAsync(
        IGameEngineAdapter adapter,
        GameProfile profile,
        IReadOnlyList<ScannedTarget> targets,
        Action<ScannedTarget>? onTargetIndexed = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int total = targets.Count;
        int completed = 0;

        // Index highest-relevance targets first so user gets search coverage on likely localization files immediately
        List<ScannedTarget> ordered = targets
            .OrderByDescending(t => t.RelevanceScore)
            .ThenBy(t => t.EstimatedSizeBytes)
            .ToList();

        foreach (ScannedTarget target in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!target.IsIndexed)
                {
                    ExtractedTargetContent extracted = await ExtractAndUpdateTargetMetadataAsync(
                        adapter, profile, target, cacheResult: _contentCache.Count < MaxCachedTargets, cancellationToken)
                        .ConfigureAwait(false);

                    SearchIndex.IndexTarget(target, extracted.Entries);
                    onTargetIndexed?.Invoke(target);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep UI responsive even if an individual asset is malformed
                target.IsIndexed = true;
                target.IsLikelyNoise = true;
                onTargetIndexed?.Invoke(target);
            }
            finally
            {
                int done = Interlocked.Increment(ref completed);
                progress?.Report(new ScanProgress("Phase2", done, total, target.DisplayName));
            }
        }
    }

    public async Task<ExtractedTargetContent> GetOrExtractTargetAsync(
        IGameEngineAdapter adapter,
        GameProfile profile,
        ScannedTarget target,
        CancellationToken cancellationToken = default)
    {
        if (_contentCache.TryGetValue(target.TargetId, out ExtractedTargetContent? cached))
        {
            return cached;
        }

        ExtractedTargetContent extracted = await ExtractAndUpdateTargetMetadataAsync(
            adapter, profile, target, cacheResult: true, cancellationToken).ConfigureAwait(false);

        SearchIndex.IndexTarget(target, extracted.Entries);
        return extracted;
    }

    private async Task<ExtractedTargetContent> ExtractAndUpdateTargetMetadataAsync(
        IGameEngineAdapter adapter,
        GameProfile profile,
        ScannedTarget target,
        bool cacheResult,
        CancellationToken cancellationToken)
    {
        ExtractedTargetContent rawContent = await adapter.ExtractTargetAsync(profile, target, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<TextEntry> classifiedEntries = LanguageDetector.AssignLanguages(
            rawContent.Entries,
            target.DisplayName,
            target.ContainerRelativePath);

        ExtractedTargetContent content = rawContent with { Entries = classifiedEntries };

        int totalCount = content.Entries.Count;
        int validCount = 0;
        int scoreSum = 0;
        var langCounts = new Dictionary<DetectedLanguage, int>();

        foreach (TextEntry entry in content.Entries)
        {
            if (!entry.IsSystemNoise)
            {
                validCount++;
                scoreSum += entry.RelevanceScore;
                langCounts.TryGetValue(entry.Language, out int existing);
                langCounts[entry.Language] = existing + 1;
            }
        }

        target.DetectedFormat = content.InnerFormat;
        target.TotalEntryCount = totalCount;
        target.ValidEntryCount = validCount;
        target.LanguageEntryCounts.Clear();
        foreach ((DetectedLanguage lang, int cnt) in langCounts)
        {
            target.LanguageEntryCounts[lang] = cnt;
        }
        target.IsIndexed = true;

        if (validCount == 0)
        {
            target.IsLikelyNoise = true;
            target.RelevanceScore = Math.Min(target.RelevanceScore, 15);
        }
        else
        {
            int avgEntryScore = scoreSum / validCount;
            target.RelevanceScore = Math.Clamp((target.RelevanceScore + avgEntryScore) / 2, 10, 100);
            if (validCount >= 3 && avgEntryScore >= 55)
            {
                target.IsLikelyNoise = false;
            }
        }

        if (cacheResult)
        {
            if (_contentCache.Count >= MaxCachedTargets)
            {
                string? firstKey = _contentCache.Keys.FirstOrDefault();
                if (firstKey is not null)
                {
                    _contentCache.TryRemove(firstKey, out _);
                }
            }

            _contentCache[target.TargetId] = content;
        }

        return content;
    }
}
