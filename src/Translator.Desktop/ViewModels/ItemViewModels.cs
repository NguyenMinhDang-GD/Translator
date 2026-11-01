using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Translator.Core.Models;

namespace Translator.Desktop.ViewModels;

public sealed partial class ScannedTargetItemViewModel : ObservableObject
{
    private static readonly IBrush LooseBrush = SolidColorBrush.Parse("#059669");
    private static readonly IBrush TextAssetBrush = SolidColorBrush.Parse("#2563EB");
    private static readonly IBrush MonoBrush = SolidColorBrush.Parse("#9333EA");
    private static readonly IBrush CodeBrush = SolidColorBrush.Parse("#D97706");

    private readonly Action? _onSelectionChanged;

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

    public string SourceBadgeText => Model.SourceKind switch
    {
        TextSourceKind.LooseFile => "LOOSE",
        TextSourceKind.TextAsset => "TEXT",
        TextSourceKind.MonoBehaviour => "MONO",
        TextSourceKind.CodeString => "CODE",
        _ => "FILE"
    };

    public IBrush SourceBadgeBrush => Model.SourceKind switch
    {
        TextSourceKind.LooseFile => LooseBrush,
        TextSourceKind.TextAsset => TextAssetBrush,
        TextSourceKind.MonoBehaviour => MonoBrush,
        TextSourceKind.CodeString => CodeBrush,
        _ => TextAssetBrush
    };

    public string ScoreBadge => $"Điểm: {Model.RelevanceScore}";

    public string EntryCountBadge => !Model.IsIndexed
        ? FormatBytes(Model.EstimatedSizeBytes)
        : $"{Model.ValidEntryCount:N0} câu ({Model.TotalEntryCount:N0} tổng)";

    public bool IsNoiseFlagged => Model.IsLikelyNoise;

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
    int RelevanceScore)
{
    public string NoiseTag => IsSystemNoise ? "[Hệ thống]" : string.Empty;
}
