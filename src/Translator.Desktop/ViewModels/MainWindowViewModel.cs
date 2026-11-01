using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Translator.Core.Abstractions;
using Translator.Core.Exporting;
using Translator.Core.Filters;
using Translator.Core.Indexing;
using Translator.Core.Models;
using Translator.Engines.Unity;

namespace Translator.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly UnityEngineAdapter _unityAdapter = new();
    private readonly TwoPhaseScanCoordinator _coordinator;
    private readonly ExtractionService _extractionService = new();

    private readonly List<ScannedTargetItemViewModel> _allTargets = [];
    private CancellationTokenSource? _scanCts;
    private IGameEngineAdapter? _activeAdapter;
    private GameProfile? _activeProfile;
    private ExtractedTargetContent? _activePreviewContent;
    private long _lastPhase2UiRefreshTicks;

    public MainWindowViewModel()
    {
        _coordinator = new TwoPhaseScanCoordinator([_unityAdapter]);
        LanguageOptions =
        [
            new LanguageFilterOption(SourceLanguageFilter.English, "1. English (Tiếng Anh)", "EN"),
            new LanguageFilterOption(SourceLanguageFilter.Chinese, "2. China (Tiếng Trung)", "CN"),
            new LanguageFilterOption(SourceLanguageFilter.Russian, "3. Russian (Tiếng Nga)", "RU"),
            new LanguageFilterOption(SourceLanguageFilter.Japanese, "4. Japan (Tiếng Nhật)", "JP"),
            new LanguageFilterOption(SourceLanguageFilter.All, "Tất cả ngôn ngữ (All)", "ALL")
        ];
        _selectedLanguageOption = LanguageOptions[0];
    }

    public IReadOnlyList<LanguageFilterOption> LanguageOptions { get; }
    public ObservableCollection<ScannedTargetItemViewModel> FilteredTargets { get; } = [];
    public ObservableCollection<PreviewEntryItem> PreviewEntries { get; } = [];
    public ObservableCollection<EncryptedFileWarning> EncryptedWarnings { get; } = [];

    [ObservableProperty]
    private LanguageFilterOption _selectedLanguageOption;

    [ObservableProperty]
    private string _gameFolderPath = string.Empty;

    [ObservableProperty]
    private string _gameBannerText = "Chưa chọn thư mục Game — Hãy bấm 'Chọn thư mục Game' để bắt đầu.";

    [ObservableProperty]
    private string _statusMessage = "Sẵn sàng.";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _includeLooseFiles = true;

    [ObservableProperty]
    private bool _includeTextAssets = true;

    [ObservableProperty]
    private bool _includeMonoBehaviours = true;

    [ObservableProperty]
    private bool _includeCodeStrings = true;

    [ObservableProperty]
    private bool _showSystemNoise = false;

    [ObservableProperty]
    private bool _filterPreviewBySearch = true;

    [ObservableProperty]
    private ScannedTargetItemViewModel? _selectedTarget;

    [ObservableProperty]
    private string _previewHeaderTitle = "Chọn một mục bên trái để xem trước nội dung câu chữ";

    [ObservableProperty]
    private string _previewHeaderMeta = string.Empty;

    [ObservableProperty]
    private string _previewRawText = string.Empty;

    [ObservableProperty]
    private bool _isPreviewLoading;

    [ObservableProperty]
    private bool _exportTranslatorJson = true;

    [ObservableProperty]
    private bool _exportCsv = true;

    [ObservableProperty]
    private bool _includeRawFile = false;

    [ObservableProperty]
    private string _selectionSummary = "Đã chọn: 0 mục";

    [ObservableProperty]
    private string _encryptedBadgeText = "0 cảnh báo mã hóa";

    [ObservableProperty]
    private bool _hasEncryptedWarnings;

    [ObservableProperty]
    private string? _lastExportDirectory;

    [ObservableProperty]
    private int _selectedRightTabIndex;

    public SourceLanguageFilter ActiveLanguageFilter => SelectedLanguageOption?.Filter ?? SourceLanguageFilter.All;

    partial void OnSelectedLanguageOptionChanged(LanguageFilterOption value)
    {
        SourceLanguageFilter filter = value?.Filter ?? SourceLanguageFilter.All;
        foreach (ScannedTargetItemViewModel item in _allTargets)
        {
            item.SetLanguageFilter(filter);
        }

        ApplyFilters(preserveSelection: true);
        UpdateActivePreviewHeader();
        RefreshActivePreviewEntries();
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilters();
        RefreshActivePreviewEntries();
    }

    partial void OnIncludeLooseFilesChanged(bool value) => ApplyFilters();
    partial void OnIncludeTextAssetsChanged(bool value) => ApplyFilters();
    partial void OnIncludeMonoBehavioursChanged(bool value) => ApplyFilters();
    partial void OnIncludeCodeStringsChanged(bool value) => ApplyFilters();

    partial void OnShowSystemNoiseChanged(bool value)
    {
        ApplyFilters();
        RefreshActivePreviewEntries();
    }

    partial void OnFilterPreviewBySearchChanged(bool value) => RefreshActivePreviewEntries();

    partial void OnSelectedTargetChanged(ScannedTargetItemViewModel? value)
    {
        _ = LoadPreviewForTargetAsync(value);
    }

    public async Task ScanGameFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        CancelActiveOperation();
        _scanCts = new CancellationTokenSource();
        CancellationToken token = _scanCts.Token;

        GameFolderPath = folderPath;
        IsBusy = true;
        ProgressPercent = 0;
        StatusMessage = "Đang nhận diện cấu trúc Game...";

        _allTargets.Clear();
        FilteredTargets.Clear();
        PreviewEntries.Clear();
        EncryptedWarnings.Clear();
        PreviewRawText = string.Empty;
        HasEncryptedWarnings = false;
        EncryptedBadgeText = "0 cảnh báo mã hóa";
        UpdateSelectionSummary();

        try
        {
            var detected = await _coordinator.DetectGameAsync(folderPath, token);
            if (detected is null)
            {
                GameBannerText = "Không nhận diện được thư mục Game Unity hợp lệ (cần thư mục chứa *_Data, StreamingAssets, .assets hoặc .bundle).";
                StatusMessage = "Không tìm thấy dữ liệu Unity trong thư mục đã chọn.";
                IsBusy = false;
                return;
            }

            (_activeAdapter, _activeProfile) = detected.Value;
            GameBannerText = $"{_activeProfile.GameName}  •  Engine: {_activeProfile.EngineName} {_activeProfile.EngineVersion}  •  Kiến trúc: {_activeProfile.ScriptingBackend}";

            var progress = new Progress<ScanProgress>(p =>
            {
                double pct = p.TotalCount > 0 ? (double)p.CompletedCount / p.TotalCount * 100.0 : 0;
                ProgressPercent = pct;
                string phaseLabel = p.Phase == "Phase1"
                    ? "Pha 1 (Quét danh sách nhanh)"
                    : "Pha 2 (Đánh chỉ mục Full-Text & Phân loại ngôn ngữ)";
                StatusMessage = $"{phaseLabel}: {p.CompletedCount:N0}/{p.TotalCount:N0} — {p.CurrentItem}";
            });

            // PHASE 1: Fast Discovery
            ScanPhase1Result phase1 = await _coordinator.RunPhase1DiscoveryAsync(_activeAdapter, _activeProfile, progress, token);

            foreach (EncryptedFileWarning warning in phase1.EncryptedWarnings)
            {
                EncryptedWarnings.Add(warning);
            }

            HasEncryptedWarnings = EncryptedWarnings.Count > 0;
            EncryptedBadgeText = $"{EncryptedWarnings.Count} cảnh báo mã hóa";

            SourceLanguageFilter currentLang = ActiveLanguageFilter;
            foreach (ScannedTarget target in phase1.Targets)
            {
                var vm = new ScannedTargetItemViewModel(target, UpdateSelectionSummary);
                vm.SetLanguageFilter(currentLang);
                _allTargets.Add(vm);
            }

            ApplyFilters();

            if (FilteredTargets.Count > 0)
            {
                SelectedTarget = FilteredTargets[0];
            }

            StatusMessage = $"Pha 1 hoàn tất ({_allTargets.Count:N0} mục). Đang chạy ngầm Pha 2 đánh chỉ mục câu thoại & nhận diện ngôn ngữ...";

            // PHASE 2: Background Deep Indexing
            await _coordinator.RunPhase2DeepIndexingAsync(
                _activeAdapter,
                _activeProfile,
                phase1.Targets,
                onTargetIndexed: indexedTarget =>
                {
                    long nowTicks = Environment.TickCount64;
                    bool shouldRebuildFilter = Interlocked.Read(ref _lastPhase2UiRefreshTicks) + 350 <= nowTicks;
                    if (shouldRebuildFilter)
                    {
                        Interlocked.Exchange(ref _lastPhase2UiRefreshTicks, nowTicks);
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        ScannedTargetItemViewModel? vm = _allTargets.FirstOrDefault(x => x.Model.TargetId == indexedTarget.TargetId);
                        vm?.NotifyMetadataUpdated();
                        if (shouldRebuildFilter)
                        {
                            ApplyFilters(preserveSelection: true);
                        }
                    });
                },
                progress: progress,
                cancellationToken: token);

            ApplyFilters(preserveSelection: true);
            ProgressPercent = 100;
            StatusMessage = $"Hoàn tất quét & phân loại ngôn ngữ 100%! Hiển thị {FilteredTargets.Count:N0} / {_allTargets.Count:N0} mục.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Đã dừng tác vụ quét.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khi quét game: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (!string.IsNullOrWhiteSpace(GameFolderPath))
        {
            await ScanGameFolderAsync(GameFolderPath);
        }
    }

    [RelayCommand]
    private void CancelActiveOperation()
    {
        try
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _scanCts = null;
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (ScannedTargetItemViewModel item in FilteredTargets)
        {
            item.IsSelected = true;
        }

        UpdateSelectionSummary();
    }

    [RelayCommand]
    private void ClearAllSelections()
    {
        foreach (ScannedTargetItemViewModel item in _allTargets)
        {
            item.IsSelected = false;
        }

        UpdateSelectionSummary();
    }

    [RelayCommand]
    private void ShowEncryptedTab()
    {
        SelectedRightTabIndex = 2;
    }

    [RelayCommand]
    private void OpenLastExportFolder()
    {
        if (string.IsNullOrWhiteSpace(LastExportDirectory) || !Directory.Exists(LastExportDirectory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LastExportDirectory,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    public List<ScannedTarget> GetTargetsForExtraction()
    {
        List<ScannedTarget> checkedTargets = _allTargets
            .Where(t => t.IsSelected)
            .Select(t => t.Model)
            .ToList();

        if (checkedTargets.Count > 0)
        {
            return checkedTargets;
        }

        if (SelectedTarget is not null)
        {
            return [SelectedTarget.Model];
        }

        return [];
    }

    public async Task<ExportSummary?> ExecuteExtractAsync(string outputFolder)
    {
        if (_activeAdapter is null || _activeProfile is null)
        {
            StatusMessage = "Chưa có dữ liệu Game để Extract.";
            return null;
        }

        List<ScannedTarget> targetsToExport = GetTargetsForExtraction();
        if (targetsToExport.Count == 0)
        {
            StatusMessage = "Hãy tick chọn ít nhất 1 mục (hoặc chọn 1 mục đang xem trước) để Extract.";
            return null;
        }

        if (!ExportTranslatorJson && !ExportCsv && !IncludeRawFile)
        {
            ExportTranslatorJson = true;
        }

        IsBusy = true;
        ProgressPercent = 0;

        SourceLanguageFilter langFilter = ActiveLanguageFilter;
        string langLabel = SelectedLanguageOption?.DisplayLabel ?? "All";

        var options = new ExportOptions
        {
            OutputDirectory = outputFolder,
            ExportTranslatorJson = ExportTranslatorJson,
            ExportCsv = ExportCsv,
            IncludeRawFile = IncludeRawFile,
            IncludeSystemNoiseEntries = ShowSystemNoise,
            LanguageFilter = langFilter
        };

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressPercent = p.TotalCount > 0 ? (double)p.CompletedCount / p.TotalCount * 100.0 : 0;
            StatusMessage = $"Đang Extract [{langLabel}] ({p.CompletedCount}/{p.TotalCount}): {p.CurrentItem}";
        });

        try
        {
            List<ScannedTarget> allModels = _allTargets.Select(t => t.Model).ToList();
            ExportSummary summary = await _extractionService.ExportTargetsAsync(
                _activeAdapter,
                _activeProfile,
                _coordinator,
                targetsToExport,
                allModels,
                options,
                progress);

            LastExportDirectory = summary.OutputDirectory;
            ProgressPercent = 100;

            string errSuffix = summary.Errors.Count > 0
                ? $" ({summary.Errors.Count} cảnh báo)"
                : string.Empty;

            StatusMessage = $"Extract [{langLabel}] thành công {summary.ExportedTargetCount:N0} mục ({summary.ExportedEntryCount:N0} câu) tới: {summary.OutputDirectory}{errSuffix}";
            return summary;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khi Extract: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilters(bool preserveSelection = true)
    {
        ScannedTargetItemViewModel? previousSelected = SelectedTarget;
        SourceLanguageFilter langFilter = ActiveLanguageFilter;

        IEnumerable<ScannedTargetItemViewModel> query = _allTargets.Where(item =>
        {
            TextSourceKind kind = item.Model.SourceKind;
            if (kind == TextSourceKind.LooseFile && !IncludeLooseFiles) return false;
            if (kind == TextSourceKind.TextAsset && !IncludeTextAssets) return false;
            if (kind == TextSourceKind.MonoBehaviour && !IncludeMonoBehaviours) return false;
            if (kind == TextSourceKind.CodeString && !IncludeCodeStrings) return false;

            if (!ShowSystemNoise)
            {
                if (item.Model.IsLikelyNoise)
                {
                    return false;
                }

                // Hide targets that are already indexed and contain 0 sentences in the selected language
                if (langFilter != SourceLanguageFilter.All &&
                    item.Model.IsIndexed &&
                    item.Model.GetValidCountForFilter(langFilter) == 0)
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                return _coordinator.SearchIndex.Matches(item.Model, SearchQuery);
            }

            return true;
        });

        List<ScannedTargetItemViewModel> sorted = query
            .OrderBy(i => i.Model.IsLikelyNoise ? 1 : 0)
            .ThenByDescending(i => i.Model.RelevanceScore)
            .ThenByDescending(i => i.Model.GetValidCountForFilter(langFilter))
            .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FilteredTargets.Clear();
        foreach (ScannedTargetItemViewModel vm in sorted)
        {
            FilteredTargets.Add(vm);
        }

        if (preserveSelection && previousSelected is not null && FilteredTargets.Contains(previousSelected))
        {
            SelectedTarget = previousSelected;
        }

        UpdateSelectionSummary();
    }

    private async Task LoadPreviewForTargetAsync(ScannedTargetItemViewModel? item)
    {
        PreviewEntries.Clear();
        PreviewRawText = string.Empty;
        _activePreviewContent = null;

        if (item is null || _activeAdapter is null || _activeProfile is null)
        {
            PreviewHeaderTitle = "Chọn một mục bên trái để xem trước nội dung câu chữ";
            PreviewHeaderMeta = string.Empty;
            return;
        }

        IsPreviewLoading = true;
        PreviewHeaderTitle = item.DisplayName;
        PreviewHeaderMeta = $"Đang giải mã dữ liệu từ {item.ContainerSubtitle}...";

        try
        {
            ExtractedTargetContent content = await _coordinator.GetOrExtractTargetAsync(
                _activeAdapter,
                _activeProfile,
                item.Model);

            item.NotifyMetadataUpdated();

            if (SelectedTarget != item)
            {
                return;
            }

            _activePreviewContent = content;
            UpdateActivePreviewHeader();
            PreviewRawText = content.RawText ?? "(Nguồn dữ liệu nhị phân cấu trúc — xem ở tab 'Bảng Câu Chữ Đã Bóc Tách')";
            RefreshActivePreviewEntries();
        }
        catch (Exception ex)
        {
            PreviewHeaderMeta = $"Không thể đọc xem trước: {ex.Message}";
        }
        finally
        {
            IsPreviewLoading = false;
        }
    }

    private void UpdateActivePreviewHeader()
    {
        if (SelectedTarget is null || _activePreviewContent is null)
        {
            return;
        }

        ScannedTarget m = SelectedTarget.Model;
        string pathIdStr = m.PrimaryPathId.HasValue ? $" • PathID: {m.PrimaryPathId.Value}" : string.Empty;
        SourceLanguageFilter langFilter = ActiveLanguageFilter;
        string langCountInfo = langFilter == SourceLanguageFilter.All
            ? $"Hợp lệ: {m.ValidEntryCount:N0} / {m.TotalEntryCount:N0} dòng"
            : $"Khớp ngôn ngữ [{SelectedLanguageOption.ShortCode}]: {m.GetValidCountForFilter(langFilter):N0} / {m.ValidEntryCount:N0} câu hợp lệ";

        PreviewHeaderMeta = $"Nguồn: {m.SourceKind} • Định dạng: {_activePreviewContent.InnerFormat}{pathIdStr} • {langCountInfo} • File: {SelectedTarget.ContainerSubtitle}";
    }

    private void RefreshActivePreviewEntries()
    {
        PreviewEntries.Clear();
        if (_activePreviewContent is null)
        {
            return;
        }

        string q = SearchQuery.Trim();
        bool hasSearch = FilterPreviewBySearch && !string.IsNullOrEmpty(q);
        SourceLanguageFilter langFilter = ActiveLanguageFilter;

        const int maxPreviewRows = 2500;
        int added = 0;

        foreach (TextEntry entry in _activePreviewContent.Entries)
        {
            if (!ShowSystemNoise && entry.IsSystemNoise)
            {
                continue;
            }

            if (!LanguageDetector.MatchesFilter(entry.Language, langFilter))
            {
                continue;
            }

            if (hasSearch &&
                !entry.OriginalText.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !entry.Key.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !entry.Context.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PreviewEntries.Add(new PreviewEntryItem(
                entry.Id,
                entry.Key,
                entry.OriginalText,
                entry.Context,
                entry.IsSystemNoise,
                entry.RelevanceScore,
                entry.Language));

            added++;
            if (added >= maxPreviewRows)
            {
                break;
            }
        }
    }

    private void UpdateSelectionSummary()
    {
        int selectedTotal = _allTargets.Count(t => t.IsSelected);
        SelectionSummary = $"Đã tick chọn: {selectedTotal:N0} mục  •  Đang hiển thị: {FilteredTargets.Count:N0} / {_allTargets.Count:N0}";
    }

    public void Dispose()
    {
        CancelActiveOperation();
        _unityAdapter.Dispose();
    }
}
