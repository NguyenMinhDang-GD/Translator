using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Translator.Core.Models;

namespace Translator.Desktop.ViewModels;

public sealed record LanguageFilterOption(
    SourceLanguageFilter Filter,
    string DisplayLabel,
    string ShortCode);

public enum PatchStatusFilter
{
    All = 0,
    Patched = 1,
    Unpatched = 2
}

public sealed record PatchStatusFilterOption(
    PatchStatusFilter Filter,
    string DisplayLabel);

public sealed partial class ScannedTargetItemViewModel : ObservableObject
{
    internal static readonly IBrush LooseBrush = SolidColorBrush.Parse("#059669");
    internal static readonly IBrush TextAssetBrush = SolidColorBrush.Parse("#2563EB");
    internal static readonly IBrush MonoBrush = SolidColorBrush.Parse("#9333EA");
    internal static readonly IBrush CodeBrush = SolidColorBrush.Parse("#D97706");

    private readonly Action? _onSelectionChanged;
    private SourceLanguageFilter _activeLanguageFilter = SourceLanguageFilter.English;

    public ScannedTargetItemViewModel(ScannedTarget model, Action? onSelectionChanged = null)
    {
        Model = model;
        _onSelectionChanged = onSelectionChanged;
    }

    public ScannedTarget Model { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        _onSelectionChanged?.Invoke();
    }

    public string DisplayName => Model.DisplayName;

    public string ContainerSubtitle => Model.BundleMemberName is null
        ? Model.ContainerRelativePath
        : $"{Model.ContainerRelativePath} → {Model.BundleMemberName}";

    public string SourceBadgeText => GetSourceBadgeText(Model.SourceKind);

    public IBrush SourceBadgeBrush => GetSourceBadgeBrush(Model.SourceKind);

    public static string GetSourceBadgeText(TextSourceKind kind) => kind switch
    {
        TextSourceKind.LooseFile => "LOOSE",
        TextSourceKind.TextAsset => "TEXT",
        TextSourceKind.MonoBehaviour => "MONO",
        TextSourceKind.CodeString => "CODE",
        _ => "FILE"
    };

    public static IBrush GetSourceBadgeBrush(TextSourceKind kind) => kind switch
    {
        TextSourceKind.LooseFile => LooseBrush,
        TextSourceKind.TextAsset => TextAssetBrush,
        TextSourceKind.MonoBehaviour => MonoBrush,
        TextSourceKind.CodeString => CodeBrush,
        _ => TextAssetBrush
    };

    public string ScoreBadge => $"Điểm: {Model.RelevanceScore}";

    public string EntryCountBadge
    {
        get
        {
            if (!Model.IsIndexed)
            {
                return FormatBytes(Model.EstimatedSizeBytes);
            }

            if (_activeLanguageFilter == SourceLanguageFilter.All)
            {
                return $"{Model.ValidEntryCount:N0} câu ({Model.TotalEntryCount:N0} tổng)";
            }

            int langCount = Model.GetValidCountForFilter(_activeLanguageFilter);
            string code = _activeLanguageFilter switch
            {
                SourceLanguageFilter.English => "EN",
                SourceLanguageFilter.Chinese => "CN",
                SourceLanguageFilter.Russian => "RU",
                SourceLanguageFilter.Japanese => "JP",
                _ => ""
            };
            return $"{langCount:N0} câu {code} ({Model.ValidEntryCount:N0} tổng)";
        }
    }

    public bool IsNoiseFlagged => Model.IsLikelyNoise;

    public void SetLanguageFilter(SourceLanguageFilter filter)
    {
        if (_activeLanguageFilter != filter)
        {
            _activeLanguageFilter = filter;
            OnPropertyChanged(nameof(EntryCountBadge));
        }
    }

    public void NotifyMetadataUpdated()
    {
        OnPropertyChanged(nameof(ScoreBadge));
        OnPropertyChanged(nameof(EntryCountBadge));
        OnPropertyChanged(nameof(IsNoiseFlagged));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

public sealed record PreviewEntryItem(
    string Id,
    string Key,
    string OriginalText,
    string Context,
    bool IsSystemNoise,
    int RelevanceScore,
    DetectedLanguage Language)
{
    public string NoiseTag => IsSystemNoise ? "[Hệ thống]" : string.Empty;

    public string LanguageBadge => Language switch
    {
        DetectedLanguage.English => "EN",
        DetectedLanguage.Chinese => "CN",
        DetectedLanguage.Russian => "RU",
        DetectedLanguage.Japanese => "JP",
        DetectedLanguage.Other => "OTHER",
        _ => "AUTO"
    };
}

public sealed record PatchStagedPackageItem(
    string PackageId,
    string DisplayName,
    string FullPath,
    string KindBadge,
    int PayloadCount,
    int TranslatedEntryCount,
    int TotalEntryCount,
    IReadOnlyList<PatchPayload> Payloads)
{
    public string SummaryBadge => $"{PayloadCount:N0} nguồn • {TranslatedEntryCount:N0}/{TotalEntryCount:N0} câu đã dịch";
}

public sealed partial class PatchTargetStatusItemViewModel : ObservableObject
{
    private static readonly IBrush PatchedBg = SolidColorBrush.Parse("#0D2821");
    private static readonly IBrush PatchedBorder = SolidColorBrush.Parse("#10B981");
    private static readonly IBrush PatchedText = SolidColorBrush.Parse("#6EE7B7");

    private static readonly IBrush PartialBg = SolidColorBrush.Parse("#3B2506");
    private static readonly IBrush PartialBorder = SolidColorBrush.Parse("#F59E0B");
    private static readonly IBrush PartialText = SolidColorBrush.Parse("#FCD34D");

    private static readonly IBrush UnpatchedBg = SolidColorBrush.Parse("#1E293B");
    private static readonly IBrush UnpatchedBorder = SolidColorBrush.Parse("#475569");
    private static readonly IBrush UnpatchedText = SolidColorBrush.Parse("#CBD5E1");

    public PatchTargetStatusItemViewModel(
        string targetKey,
        string displayName,
        string containerSubtitle,
        TextSourceKind sourceKind,
        ScannedTarget? scannedTarget,
        PatchPayload? matchedPayload,
        int patchedCount,
        int totalCount,
        bool isAppliedToDisk)
    {
        TargetKey = targetKey;
        DisplayName = displayName;
        ContainerSubtitle = containerSubtitle;
        SourceKind = sourceKind;
        ScannedTarget = scannedTarget;
        MatchedPayload = matchedPayload;
        PatchedCount = patchedCount;
        TotalCount = Math.Max(totalCount, patchedCount);
        IsAppliedToDisk = isAppliedToDisk;
    }

    public string TargetKey { get; }
    public string DisplayName { get; }
    public string ContainerSubtitle { get; }
    public TextSourceKind SourceKind { get; }
    public ScannedTarget? ScannedTarget { get; }
    public PatchPayload? MatchedPayload { get; }
    public int PatchedCount { get; }
    public int TotalCount { get; }
    public bool IsAppliedToDisk { get; }

    public bool HasAnyPatched => PatchedCount > 0;
    public bool IsFullyPatched => TotalCount > 0 && PatchedCount >= TotalCount;
    public bool IsPartiallyPatched => PatchedCount > 0 && PatchedCount < TotalCount;
    public int UnpatchedCount => Math.Max(0, TotalCount - PatchedCount);

    public double ProgressPercent => TotalCount > 0
        ? Math.Clamp((double)PatchedCount / TotalCount * 100.0, 0.0, 100.0)
        : (PatchedCount > 0 ? 100.0 : 0.0);

    public string FormattedPercent => HasAnyPatched && ProgressPercent < 1.0
        ? "<1%"
        : $"{ProgressPercent:F0}%";

    public string ProgressLabel => TotalCount > 0
        ? $"{PatchedCount:N0} / {TotalCount:N0} câu ({FormattedPercent})"
        : (PatchedCount > 0 ? $"{PatchedCount:N0} câu đã dịch" : "0 câu đã vá");

    public string SourceBadgeText => ScannedTargetItemViewModel.GetSourceBadgeText(SourceKind);
    public IBrush SourceBadgeBrush => ScannedTargetItemViewModel.GetSourceBadgeBrush(SourceKind);

    public string StatusBadgeText
    {
        get
        {
            if (IsFullyPatched)
            {
                return IsAppliedToDisk ? "ĐÃ VÁ 100%" : "SẴN SÀNG 100%";
            }

            if (HasAnyPatched)
            {
                return IsAppliedToDisk
                    ? $"ĐÃ VÁ ({PatchedCount:N0} câu • {FormattedPercent})"
                    : $"ĐÃ DỊCH ({PatchedCount:N0} câu • {FormattedPercent})";
            }

            return "CHƯA VÁ";
        }
    }

    public IBrush StatusBadgeBg => IsFullyPatched
        ? PatchedBg
        : (HasAnyPatched ? PartialBg : UnpatchedBg);

    public IBrush StatusBadgeBorder => IsFullyPatched
        ? PatchedBorder
        : (HasAnyPatched ? PartialBorder : UnpatchedBorder);

    public IBrush StatusBadgeForeground => IsFullyPatched
        ? PatchedText
        : (HasAnyPatched ? PartialText : UnpatchedText);
}

public sealed record PatchSentenceComparisonItem(
    string Id,
    string Key,
    string OriginalText,
    string TranslatedText,
    string Context,
    bool IsPatched,
    bool IsAppliedToDisk)
{
    private static readonly IBrush PatchedBg = SolidColorBrush.Parse("#0D2821");
    private static readonly IBrush PatchedBorder = SolidColorBrush.Parse("#10B981");
    private static readonly IBrush PatchedText = SolidColorBrush.Parse("#6EE7B7");

    private static readonly IBrush UnpatchedBg = SolidColorBrush.Parse("#3B2506");
    private static readonly IBrush UnpatchedBorder = SolidColorBrush.Parse("#D97706");
    private static readonly IBrush UnpatchedText = SolidColorBrush.Parse("#FCD34D");

    public string DisplayTranslation => IsPatched
        ? TranslatedText
        : "(Chưa có bản dịch — giữ nguyên câu gốc)";

    public string StatusBadgeText => IsPatched
        ? (IsAppliedToDisk ? "ĐÃ VÁ" : "ĐÃ DỊCH")
        : "CHƯA VÁ";

    public IBrush StatusBadgeBg => IsPatched ? PatchedBg : UnpatchedBg;
    public IBrush StatusBadgeBorder => IsPatched ? PatchedBorder : UnpatchedBorder;
    public IBrush StatusBadgeForeground => IsPatched ? PatchedText : UnpatchedText;
}
