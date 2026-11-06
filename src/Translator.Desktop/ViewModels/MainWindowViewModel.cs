using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Translator.Core.Abstractions;
using Translator.Core.Backup;
using Translator.Core.Exporting;
using Translator.Core.Filters;
using Translator.Core.Indexing;
using Translator.Core.Models;
using Translator.Core.Patching;
using Translator.Engines.Unity;
using Translator.Engines.Unity.Patching;

namespace Translator.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly UnityEngineAdapter _unityAdapter = new();
    private readonly UnityPatcher _unityPatcher = new();
    private readonly IBackupVaultService _backupVault = new BackupVaultService();
    private readonly IPatchPackageLoader _packageLoader = new TranslationPackageLoader();
    private readonly TwoPhaseScanCoordinator _coordinator;
    private readonly ExtractionService _extractionService = new();

    private readonly List<ScannedTargetItemViewModel> _allTargets = [];
    private readonly List<PatchTargetStatusItemViewModel> _allPatchTargetStatuses = [];
    private readonly List<PatchSentenceComparisonItem> _activePatchTargetAllSentences = [];
    private readonly HashSet<string> _appliedTargetKeysOnDisk = new(StringComparer.OrdinalIgnoreCase);

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

        TargetPatchFilterOptions =
        [
            new PatchStatusFilterOption(PatchStatusFilter.All, "Tất cả nguồn dữ liệu"),
            new PatchStatusFilterOption(PatchStatusFilter.Patched, "Nguồn đã vá / có bản dịch"),
            new PatchStatusFilterOption(PatchStatusFilter.Unpatched, "Nguồn chưa vá")
        ];
        _selectedTargetPatchFilterOption = TargetPatchFilterOptions[0];

        SentencePatchFilterOptions =
        [
            new PatchStatusFilterOption(PatchStatusFilter.All, "Tất cả câu (Đã vá + Chưa vá)"),
            new PatchStatusFilterOption(PatchStatusFilter.Patched, "Chỉ câu ĐÃ VÁ / ĐÃ DỊCH"),
            new PatchStatusFilterOption(PatchStatusFilter.Unpatched, "Chỉ câu CHƯA VÁ (Giữ nguyên gốc)")
        ];
        _selectedSentencePatchFilterOption = SentencePatchFilterOptions[0];
    }

    public IReadOnlyList<LanguageFilterOption> LanguageOptions { get; }
    public IReadOnlyList<PatchStatusFilterOption> TargetPatchFilterOptions { get; }
    public IReadOnlyList<PatchStatusFilterOption> SentencePatchFilterOptions { get; }

    public ObservableCollection<ScannedTargetItemViewModel> FilteredTargets { get; } = [];
    public ObservableCollection<PreviewEntryItem> PreviewEntries { get; } = [];
    public ObservableCollection<EncryptedFileWarning> EncryptedWarnings { get; } = [];

    // Page 3 Collections
    public ObservableCollection<PatchStagedPackageItem> StagedPatchPackages { get; } = [];
    public ObservableCollection<PatchTargetStatusItemViewModel> FilteredPatchTargets { get; } = [];
    public ObservableCollection<PatchSentenceComparisonItem> PatchComparisonEntries { get; } = [];

    // =========================================================================
    // LEFT SIDEBAR NAVIGATION STATE (3 MAIN PAGES)
    // =========================================================================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanPageActive))]
    [NotifyPropertyChangedFor(nameof(IsExplorerPageActive))]
    [NotifyPropertyChangedFor(nameof(IsPatchPageActive))]
    private int _currentPageIndex;

    public bool IsScanPageActive => CurrentPageIndex == 0;
    public bool IsExplorerPageActive => CurrentPageIndex == 1;
    public bool IsPatchPageActive => CurrentPageIndex == 2;

    // =========================================================================
    // PAGE 3 SUB-NAVIGATION STATE (2 SCREENS ON RIGHT PANEL)
    // =========================================================================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPatchSelectScreenActive))]
    [NotifyPropertyChangedFor(nameof(IsPatchStatusScreenActive))]
    private int _patchSubPageIndex;

    public bool IsPatchSelectScreenActive => PatchSubPageIndex == 0;
    public bool IsPatchStatusScreenActive => PatchSubPageIndex == 1;

    // =========================================================================
    // SCANNER & OVERVIEW METRICS (PAGE 1 & SIDEBAR BADGES)
    // =========================================================================
    [ObservableProperty]
    private int _totalScannedTargetsCount;

    [ObservableProperty]
    private int _looseFilesCount;

    [ObservableProperty]
    private int _textAssetsCount;

    [ObservableProperty]
    private int _monoBehavioursCount;

    [ObservableProperty]
    private int _codeStringsCount;

    [ObservableProperty]
    private int _totalValidEntriesCount;

    [ObservableProperty]
    private LanguageFilterOption _selectedLanguageOption;

    [ObservableProperty]
    private string _gameFolderPath = string.Empty;

    [ObservableProperty]
    private string _gameBannerText = "Chưa chọn thư mục Game — Hãy bấm 'Chọn thư mục Game' ở Trang 1 để bắt đầu.";

    [ObservableProperty]
    private string _statusMessage = "Sẵn sàng.";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _isBusy;

    // =========================================================================
    // EXPLORER, PREVIEW & EXTRACTOR STATE (PAGE 2)
    // =========================================================================
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
    private bool _useSmartFilter = true;

    public bool ShowSystemNoise => !UseSmartFilter;

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
    private bool _includeRawFile;

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

    // =========================================================================
    // PATCHER, BACKUP VAULT & 2-LEVEL STATUS TRACKER STATE (PAGE 3)
    // =========================================================================
    [ObservableProperty]
    private bool _hasOriginalBackup;

    [ObservableProperty]
    private string _vaultStatusText = "Kho sao lưu (.translator_backup): Chưa có";

    [ObservableProperty]
    private string _stagedPackagesSummary = "Chưa chọn file hoặc thư mục bản dịch nào.";

    [ObservableProperty]
    private string _patchCoverageSummaryText = "Đã phủ bản dịch: 0 / 0 nguồn • 0 câu đã dịch";

    [ObservableProperty]
    private int _patchedTargetsCount;

    [ObservableProperty]
    private int _unpatchedTargetsCount;

    [ObservableProperty]
    private int _patchedSentencesTotalCount;

    [ObservableProperty]
    private int _unpatchedSentencesTotalCount;

    [ObservableProperty]
    private PatchStatusFilterOption _selectedTargetPatchFilterOption;

    [ObservableProperty]
    private PatchStatusFilterOption _selectedSentencePatchFilterOption;

    [ObservableProperty]
    private string _patchSearchQuery = string.Empty;

    [ObservableProperty]
    private PatchTargetStatusItemViewModel? _selectedPatchTarget;

    [ObservableProperty]
    private string _selectedPatchTargetHeader = "Chọn một nguồn dữ liệu bên trái để đối chiếu từng câu Đã vá / Chưa vá";

    [ObservableProperty]
    private string _selectedPatchTargetMeta = string.Empty;

    [ObservableProperty]
    private bool _isPatchComparisonLoading;

    public bool HasActiveGame => _activeProfile is not null;
    public bool HasStagedPackages => StagedPatchPackages.Count > 0;

    public SourceLanguageFilter ActiveLanguageFilter => SelectedLanguageOption?.Filter ?? SourceLanguageFilter.All;

    // =========================================================================
    // NAVIGATION COMMANDS
    // =========================================================================
    [RelayCommand]
    private void ShowScanPage() => CurrentPageIndex = 0;

    [RelayCommand]
    private void ShowExplorerPage() => CurrentPageIndex = 1;

    [RelayCommand]
    private void ShowPatchPage() => CurrentPageIndex = 2;

    [RelayCommand]
    private void ShowPatchSelectScreen() => PatchSubPageIndex = 0;

    [RelayCommand]
    private void ShowPatchStatusScreen()
    {
        PatchSubPageIndex = 1;
        RebuildPatchStatusOverview();
    }

    // =========================================================================
    // PROPERTY CHANGE HANDLERS
    // =========================================================================
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

    partial void OnUseSmartFilterChanged(bool value)
    {
        ApplyFilters();
        RefreshActivePreviewEntries();
    }

    partial void OnFilterPreviewBySearchChanged(bool value) => RefreshActivePreviewEntries();

    partial void OnSelectedTargetChanged(ScannedTargetItemViewModel? value)
    {
        _ = LoadPreviewForTargetAsync(value);
    }

    partial void OnSelectedTargetPatchFilterOptionChanged(PatchStatusFilterOption value)
    {
        ApplyPatchTargetFilter(preserveSelection: true);
    }

    partial void OnSelectedSentencePatchFilterOptionChanged(PatchStatusFilterOption value)
    {
        RefreshFilteredPatchSentences();
    }

    partial void OnPatchSearchQueryChanged(string value)
    {
        RefreshFilteredPatchSentences();
    }

    partial void OnSelectedPatchTargetChanged(PatchTargetStatusItemViewModel? value)
    {
        _ = LoadPatchComparisonForTargetAsync(value);
    }

    // =========================================================================
    // PAGE 1: SCAN GAME FOLDER
    // =========================================================================
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
        _appliedTargetKeysOnDisk.Clear();
        PreviewRawText = string.Empty;
        HasEncryptedWarnings = false;
        EncryptedBadgeText = "0 cảnh báo mã hóa";
        UpdateOverviewMetrics();
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
            OnPropertyChanged(nameof(HasActiveGame));
            GameBannerText = $"{_activeProfile.GameName}  •  Engine: {_activeProfile.EngineName} {_activeProfile.EngineVersion}  •  Kiến trúc: {_activeProfile.ScriptingBackend}";
            await RefreshVaultStatusAsync();

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

            UpdateOverviewMetrics();
            ApplyFilters();
            RebuildPatchStatusOverview();

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
                            UpdateOverviewMetrics();
                            ApplyFilters(preserveSelection: true);
                        }
                    });
                },
                progress: progress,
                cancellationToken: token);

            UpdateOverviewMetrics();
            ApplyFilters(preserveSelection: true);
            RebuildPatchStatusOverview();
            ProgressPercent = 100;
            StatusMessage = $"Hoàn tất quét & phân loại ngôn ngữ 100%! Phát hiện {_allTargets.Count:N0} nguồn ({TotalValidEntriesCount:N0} câu hợp lệ).";
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

    private void UpdateOverviewMetrics()
    {
        TotalScannedTargetsCount = _allTargets.Count;
        LooseFilesCount = _allTargets.Count(t => t.Model.SourceKind == TextSourceKind.LooseFile);
        TextAssetsCount = _allTargets.Count(t => t.Model.SourceKind == TextSourceKind.TextAsset);
        MonoBehavioursCount = _allTargets.Count(t => t.Model.SourceKind == TextSourceKind.MonoBehaviour);
        CodeStringsCount = _allTargets.Count(t => t.Model.SourceKind == TextSourceKind.CodeString);
        TotalValidEntriesCount = _allTargets.Sum(t => t.Model.ValidEntryCount);
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

    // =========================================================================
    // PAGE 2: EXPLORER, PREVIEW & EXTRACT
    // =========================================================================
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
        CurrentPageIndex = 0;
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

    // =========================================================================
    // PAGE 3: BACKUP VAULT, STAGING & ATOMIC PATCHER
    // =========================================================================
    public async Task RefreshVaultStatusAsync()
    {
        if (_activeProfile is null)
        {
            HasOriginalBackup = false;
            VaultStatusText = "Kho sao lưu (.translator_backup): Chưa chọn Game";
            return;
        }

        try
        {
            VaultStatus status = await _backupVault.GetVaultStatusAsync(_activeProfile);
            HasOriginalBackup = status.HasBackup;
            if (status.HasBackup)
            {
                double mb = status.TotalSizeBytes / (1024.0 * 1024.0);
                string sizeStr = mb >= 1.0 ? $"{mb:F1} MB" : $"{status.TotalSizeBytes / 1024.0:F0} KB";
                VaultStatusText = $"Kho gốc (.translator_backup): Đang bảo vệ {status.BackedUpFileCount} file ({sizeStr})";
            }
            else
            {
                VaultStatusText = "Kho sao lưu (.translator_backup): Nguyên bản (0 file đã can thiệp)";
            }
        }
        catch
        {
            HasOriginalBackup = false;
            VaultStatusText = "Kho sao lưu (.translator_backup): Chưa có";
        }
    }

    [RelayCommand]
    public async Task RestoreOriginalAsync()
    {
        if (_activeProfile is null)
        {
            StatusMessage = "Vui lòng chọn thư mục Game trước khi Khôi phục gốc.";
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressPercent = p.TotalCount > 0 ? (double)p.CompletedCount / p.TotalCount * 100.0 : 0;
            StatusMessage = $"Đang khôi phục gốc ({p.CompletedCount}/{p.TotalCount}): {p.CurrentItem}";
        });

        try
        {
            RestoreSummary summary = await _backupVault.RestoreOriginalAsync(_activeProfile, progress);
            _coordinator.InvalidateCache();
            _appliedTargetKeysOnDisk.Clear();
            await RefreshVaultStatusAsync();
            await LoadPreviewForTargetAsync(SelectedTarget);
            RebuildPatchStatusOverview();

            ProgressPercent = 100;
            if (summary.Errors.Count > 0 && summary.RestoredFileCount == 0)
            {
                StatusMessage = $"Khôi phục gốc: {summary.Errors[0]}";
            }
            else
            {
                double mb = summary.TotalBytesRestored / (1024.0 * 1024.0);
                string errNote = summary.Errors.Count > 0 ? $" ({summary.Errors.Count} lỗi)" : string.Empty;
                StatusMessage = $"Đã khôi phục nguyên trạng {summary.RestoredFileCount} file gốc ({mb:F1} MB) từ .translator_backup/! Kho backup vẫn được giữ nguyên{errNote}.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khi khôi phục gốc: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StagePatchFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        try
        {
            IReadOnlyList<PatchPayload> payloads = await _packageLoader.LoadFromDirectoryAsync(folderPath);
            if (payloads.Count == 0)
            {
                StatusMessage = $"Không tìm thấy gói bản dịch hợp lệ nào trong thư mục: {folderPath}";
                return;
            }

            AddOrReplaceStagedPackage(
                fullPath: Path.GetFullPath(folderPath),
                displayName: Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                kindBadge: "FOLDER",
                payloads: payloads);

            StatusMessage = $"Đã nạp thư mục bản dịch '{Path.GetFileName(folderPath)}' ({payloads.Count:N0} nguồn dữ liệu).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khi đọc thư mục bản dịch: {ex.Message}";
        }
    }

    public async Task StagePatchFilesAsync(IEnumerable<string> filePaths)
    {
        int addedFiles = 0;
        foreach (string filePath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                continue;
            }

            try
            {
                IReadOnlyList<PatchPayload> payloads = await _packageLoader.LoadFromFileAsync(
                    filePath,
                    optionalManifestPath: null,
                    fallbackTarget: SelectedTarget?.Model,
                    fallbackProfile: _activeProfile);

                if (payloads.Count == 0)
                {
                    continue;
                }

                string ext = filePath.EndsWith(".translator.json", StringComparison.OrdinalIgnoreCase)
                    ? "JSON"
                    : (filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "CSV" : "RAW");

                AddOrReplaceStagedPackage(
                    fullPath: Path.GetFullPath(filePath),
                    displayName: Path.GetFileName(filePath),
                    kindBadge: ext,
                    payloads: payloads);

                addedFiles++;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi khi nạp file '{Path.GetFileName(filePath)}': {ex.Message}";
            }
        }

        if (addedFiles > 0)
        {
            StatusMessage = $"Đã thêm {addedFiles} file bản dịch vào danh sách chuẩn bị vá.";
        }
    }

    private void AddOrReplaceStagedPackage(
        string fullPath,
        string displayName,
        string kindBadge,
        IReadOnlyList<PatchPayload> payloads)
    {
        for (int i = StagedPatchPackages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(StagedPatchPackages[i].FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                StagedPatchPackages.RemoveAt(i);
            }
        }

        int translatedCount = 0;
        int totalCount = 0;
        foreach (PatchPayload p in payloads)
        {
            int countInPayload = CountTranslatedEntriesInPayload(p);
            translatedCount += countInPayload;
            totalCount += Math.Max(p.Package.Entries.Count, countInPayload);
        }

        StagedPatchPackages.Add(new PatchStagedPackageItem(
            PackageId: Guid.NewGuid().ToString("N"),
            DisplayName: displayName,
            FullPath: fullPath,
            KindBadge: kindBadge,
            PayloadCount: payloads.Count,
            TranslatedEntryCount: translatedCount,
            TotalEntryCount: totalCount,
            Payloads: payloads));

        UpdateStagedSummaryAndOverview();
    }

    [RelayCommand]
    private void RemoveStagedPackage(PatchStagedPackageItem? item)
    {
        if (item is null)
        {
            return;
        }

        StagedPatchPackages.Remove(item);
        UpdateStagedSummaryAndOverview();
    }

    [RelayCommand]
    private void ClearStagedPackages()
    {
        StagedPatchPackages.Clear();
        UpdateStagedSummaryAndOverview();
        StatusMessage = "Đã xóa danh sách gói bản dịch đang chọn.";
    }

    [RelayCommand]
    private void ClearPatchSearch()
    {
        PatchSearchQuery = string.Empty;
    }

    private void UpdateStagedSummaryAndOverview()
    {
        OnPropertyChanged(nameof(HasStagedPackages));
        if (StagedPatchPackages.Count == 0)
        {
            StagedPackagesSummary = "Chưa chọn file hoặc thư mục bản dịch nào.";
        }
        else
        {
            int totalPayloads = StagedPatchPackages.Sum(p => p.PayloadCount);
            int totalTranslated = StagedPatchPackages.Sum(p => p.TranslatedEntryCount);
            StagedPackagesSummary = $"Đã nạp: {StagedPatchPackages.Count} gói ({totalPayloads:N0} nguồn • {totalTranslated:N0} câu dịch sẵn sàng vá)";
        }

        RebuildPatchStatusOverview();
    }

    [RelayCommand]
    public async Task<PatchSummary?> ExecuteStagedPatchesAsync()
    {
        if (_activeProfile is null)
        {
            StatusMessage = "Vui lòng chọn thư mục Game (ở Trang 1) trước khi thực thi Vá bản dịch.";
            return null;
        }

        List<PatchPayload> allPayloads = GetConsolidatedStagedPayloads();
        if (allPayloads.Count == 0)
        {
            StatusMessage = "Vui lòng thêm ít nhất 1 Thư mục hoặc File bản dịch vào danh sách trước khi bấm Vá.";
            return null;
        }

        return await ExecutePatchPayloadsInternalAsync(allPayloads);
    }

    public async Task<PatchSummary?> ExecutePatchFromFolderAsync(string folderPath)
    {
        await StagePatchFolderAsync(folderPath);
        if (_activeProfile is null)
        {
            StatusMessage = "Đã nạp thư mục bản dịch. Hãy chọn thư mục Game (Trang 1) để thực thi Vá.";
            return null;
        }

        List<PatchPayload> payloads = GetConsolidatedStagedPayloads();
        return payloads.Count > 0 ? await ExecutePatchPayloadsInternalAsync(payloads) : null;
    }

    public async Task<PatchSummary?> ExecutePatchFromFileAsync(string filePath)
    {
        await StagePatchFilesAsync([filePath]);
        if (_activeProfile is null)
        {
            StatusMessage = "Đã nạp file bản dịch. Hãy chọn thư mục Game (Trang 1) để thực thi Vá.";
            return null;
        }

        List<PatchPayload> payloads = GetConsolidatedStagedPayloads();
        return payloads.Count > 0 ? await ExecutePatchPayloadsInternalAsync(payloads) : null;
    }

    private List<PatchPayload> GetConsolidatedStagedPayloads()
    {
        var map = new Dictionary<string, PatchPayload>(StringComparer.OrdinalIgnoreCase);
        foreach (PatchStagedPackageItem pkg in StagedPatchPackages)
        {
            foreach (PatchPayload payload in pkg.Payloads)
            {
                string key = GetPayloadMatchKey(payload);
                map[key] = payload;
            }
        }

        return map.Values.ToList();
    }

    private async Task<PatchSummary?> ExecutePatchPayloadsInternalAsync(IReadOnlyList<PatchPayload> payloads)
    {
        if (_activeProfile is null)
        {
            IsBusy = false;
            return null;
        }

        IsBusy = true;
        ProgressPercent = 0;

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressPercent = p.TotalCount > 0 ? (double)p.CompletedCount / p.TotalCount * 100.0 : 0;
            StatusMessage = $"Đang sao lưu SHA-256 & vá nguyên tử ({p.CompletedCount}/{p.TotalCount}): {p.CurrentItem}";
        });

        try
        {
            PatchSummary summary = await _unityPatcher.PatchAsync(
                _activeProfile,
                payloads,
                _backupVault,
                new PatchOptions { VerifyOriginalSha256 = true },
                progress);

            if (summary.PatchedContainerCount > 0)
            {
                foreach (PatchPayload payload in payloads)
                {
                    if (CountTranslatedEntriesInPayload(payload) > 0)
                    {
                        _appliedTargetKeysOnDisk.Add(GetPayloadMatchKey(payload));
                    }
                }
            }

            _coordinator.InvalidateCache();
            await RefreshVaultStatusAsync();
            await LoadPreviewForTargetAsync(SelectedTarget);
            RebuildPatchStatusOverview();

            ProgressPercent = 100;
            if (summary.Errors.Count > 0 && summary.PatchedContainerCount == 0)
            {
                StatusMessage = $"Lỗi khi vá Game: {summary.Errors[0]}";
            }
            else if (summary.PatchedContainerCount == 0 && summary.Warnings.Count > 0)
            {
                StatusMessage = summary.Warnings[0];
            }
            else
            {
                string warnSuffix = summary.Warnings.Count > 0 ? $" ({summary.Warnings.Count} cảnh báo)" : string.Empty;
                string errSuffix = summary.Errors.Count > 0 ? $" ({summary.Errors.Count} lỗi)" : string.Empty;
                StatusMessage = $"Vá Game thành công! Đã cập nhật {summary.AppliedEntryCount:N0} câu dịch vào {summary.PatchedContainerCount} file tài nguyên (Bản gốc an toàn tại .translator_backup/){warnSuffix}{errSuffix}.";
            }

            return summary;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khi vá Game: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // =========================================================================
    // PAGE 3 SCREEN 2: 2-LEVEL PATCHED VS UNPATCHED TRACKER
    // =========================================================================
    private void RebuildPatchStatusOverview()
    {
        PatchTargetStatusItemViewModel? previousSelected = SelectedPatchTarget;
        List<PatchPayload> stagedPayloads = GetConsolidatedStagedPayloads();

        var payloadByKey = new Dictionary<string, PatchPayload>(StringComparer.OrdinalIgnoreCase);
        foreach (PatchPayload p in stagedPayloads)
        {
            payloadByKey[GetPayloadMatchKey(p)] = p;
        }

        _allPatchTargetStatuses.Clear();
        var matchedPayloadKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Build status items for all scanned targets in the active game
        foreach (ScannedTargetItemViewModel targetVm in _allTargets)
        {
            ScannedTarget target = targetVm.Model;
            string targetKey = GetTargetMatchKey(target);
            PatchPayload? matchedPayload = FindMatchingPayload(target, payloadByKey, out string? matchedKey);
            if (matchedKey is not null)
            {
                matchedPayloadKeys.Add(matchedKey);
            }

            int totalCount = target.IsIndexed && target.ValidEntryCount > 0
                ? target.ValidEntryCount
                : (matchedPayload?.Package.Entries.Count ?? 0);

            int patchedCount = matchedPayload is not null
                ? CountTranslatedEntriesInPayload(matchedPayload, totalCount)
                : 0;

            bool isApplied = _appliedTargetKeysOnDisk.Contains(targetKey) ||
                             (matchedKey is not null && _appliedTargetKeysOnDisk.Contains(matchedKey));

            _allPatchTargetStatuses.Add(new PatchTargetStatusItemViewModel(
                targetKey: targetKey,
                displayName: target.DisplayName,
                containerSubtitle: targetVm.ContainerSubtitle,
                sourceKind: target.SourceKind,
                scannedTarget: target,
                matchedPayload: matchedPayload,
                patchedCount: patchedCount,
                totalCount: Math.Max(totalCount, patchedCount),
                isAppliedToDisk: isApplied));
        }

        // 2. Also include any staged payloads that were loaded without a matching scanned target
        foreach (var kvp in payloadByKey)
        {
            if (matchedPayloadKeys.Contains(kvp.Key))
            {
                continue;
            }

            PatchPayload payload = kvp.Value;
            TranslationPackage pkg = payload.Package;
            int patchedCount = CountTranslatedEntriesInPayload(payload);
            int totalCount = Math.Max(pkg.Entries.Count, patchedCount);
            string subtitle = pkg.BundleMemberName is null
                ? pkg.ContainerRelativePath
                : $"{pkg.ContainerRelativePath} → {pkg.BundleMemberName}";

            _allPatchTargetStatuses.Add(new PatchTargetStatusItemViewModel(
                targetKey: kvp.Key,
                displayName: pkg.DisplayName,
                containerSubtitle: subtitle,
                sourceKind: pkg.SourceKind,
                scannedTarget: null,
                matchedPayload: payload,
                patchedCount: patchedCount,
                totalCount: totalCount,
                isAppliedToDisk: _appliedTargetKeysOnDisk.Contains(kvp.Key)));
        }

        PatchedTargetsCount = _allPatchTargetStatuses.Count(x => x.HasAnyPatched);
        UnpatchedTargetsCount = _allPatchTargetStatuses.Count(x => !x.HasAnyPatched);
        PatchedSentencesTotalCount = _allPatchTargetStatuses.Sum(x => x.PatchedCount);
        UnpatchedSentencesTotalCount = _allPatchTargetStatuses.Sum(x => x.UnpatchedCount);

        int totalTargets = _allPatchTargetStatuses.Count;
        int totalSentences = PatchedSentencesTotalCount + UnpatchedSentencesTotalCount;
        double pct = totalSentences > 0 ? (double)PatchedSentencesTotalCount / totalSentences * 100.0 : 0.0;

        PatchCoverageSummaryText = $"Đã có bản dịch/vá: {PatchedTargetsCount:N0} / {totalTargets:N0} nguồn  •  Đã dịch: {PatchedSentencesTotalCount:N0} / {totalSentences:N0} câu ({pct:F0}%)  •  Chưa vá: {UnpatchedSentencesTotalCount:N0} câu";

        ApplyPatchTargetFilter(preserveSelection: true, previousKey: previousSelected?.TargetKey);
    }

    private void ApplyPatchTargetFilter(bool preserveSelection = true, string? previousKey = null)
    {
        string? keyToRestore = previousKey ?? SelectedPatchTarget?.TargetKey;
        PatchStatusFilter filter = SelectedTargetPatchFilterOption?.Filter ?? PatchStatusFilter.All;

        IEnumerable<PatchTargetStatusItemViewModel> query = _allPatchTargetStatuses.Where(item => filter switch
        {
            PatchStatusFilter.Patched => item.HasAnyPatched,
            PatchStatusFilter.Unpatched => !item.HasAnyPatched,
            _ => true
        });

        List<PatchTargetStatusItemViewModel> sorted = query
            .OrderByDescending(x => x.HasAnyPatched ? 1 : 0)
            .ThenByDescending(x => x.ProgressPercent)
            .ThenByDescending(x => x.TotalCount)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FilteredPatchTargets.Clear();
        foreach (PatchTargetStatusItemViewModel item in sorted)
        {
            FilteredPatchTargets.Add(item);
        }

        if (preserveSelection && keyToRestore is not null)
        {
            PatchTargetStatusItemViewModel? restored = FilteredPatchTargets.FirstOrDefault(
                x => string.Equals(x.TargetKey, keyToRestore, StringComparison.OrdinalIgnoreCase));
            if (restored is not null)
            {
                SelectedPatchTarget = restored;
                _ = LoadPatchComparisonForTargetAsync(restored);
                return;
            }
        }

        SelectedPatchTarget = FilteredPatchTargets.FirstOrDefault();
    }

    private async Task LoadPatchComparisonForTargetAsync(PatchTargetStatusItemViewModel? targetItem)
    {
        _activePatchTargetAllSentences.Clear();
        PatchComparisonEntries.Clear();

        if (targetItem is null)
        {
            SelectedPatchTargetHeader = "Chọn một nguồn dữ liệu bên trái để đối chiếu từng câu Đã vá / Chưa vá";
            SelectedPatchTargetMeta = string.Empty;
            return;
        }

        IsPatchComparisonLoading = true;
        SelectedPatchTargetHeader = targetItem.DisplayName;
        SelectedPatchTargetMeta = $"Đang đối chiếu câu gốc và câu đã dịch cho {targetItem.ContainerSubtitle}...";

        try
        {
            PatchPayload? payload = targetItem.MatchedPayload;
            var translationById = new Dictionary<string, TextEntry>(StringComparer.OrdinalIgnoreCase);
            var translationByKey = new Dictionary<string, TextEntry>(StringComparer.OrdinalIgnoreCase);
            var allTranslatedItemsInPayload = new List<TextEntry>();
            var consumedTranslatedItems = new HashSet<TextEntry>();

            if (payload is not null)
            {
                foreach (TextEntry tItem in payload.Package.Entries)
                {
                    bool hasTrans = !string.IsNullOrWhiteSpace(tItem.TranslatedText);
                    if (hasTrans)
                    {
                        allTranslatedItemsInPayload.Add(tItem);
                    }

                    if (!string.IsNullOrWhiteSpace(tItem.Id))
                    {
                        if (hasTrans || !translationById.ContainsKey(tItem.Id))
                        {
                            translationById[tItem.Id] = tItem;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(tItem.Key))
                    {
                        if (hasTrans || !translationByKey.ContainsKey(tItem.Key))
                        {
                            translationByKey[tItem.Key] = tItem;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(tItem.Location.FieldOrInnerKey))
                    {
                        if (hasTrans || !translationByKey.ContainsKey(tItem.Location.FieldOrInnerKey))
                        {
                            translationByKey[tItem.Location.FieldOrInnerKey] = tItem;
                        }
                    }
                }
            }

            if (targetItem.ScannedTarget is not null && _activeAdapter is not null && _activeProfile is not null)
            {
                ExtractedTargetContent content = await _coordinator.GetOrExtractTargetAsync(
                    _activeAdapter,
                    _activeProfile,
                    targetItem.ScannedTarget);

                if (SelectedPatchTarget != targetItem)
                {
                    return;
                }

                bool isRawPatchedAll = payload is { IsRawOverride: true } && payload.Package.Entries.Count == 0;

                foreach (TextEntry entry in content.Entries)
                {
                    TextEntry? matchedTrans = null;
                    if (!string.IsNullOrWhiteSpace(entry.Id) && translationById.TryGetValue(entry.Id, out TextEntry? byId))
                    {
                        matchedTrans = byId;
                    }
                    else if (!string.IsNullOrWhiteSpace(entry.Key) && translationByKey.TryGetValue(entry.Key, out TextEntry? byKey))
                    {
                        matchedTrans = byKey;
                    }
                    else if (!string.IsNullOrWhiteSpace(entry.Location.FieldOrInnerKey) &&
                             translationByKey.TryGetValue(entry.Location.FieldOrInnerKey, out TextEntry? byLoc))
                    {
                        matchedTrans = byLoc;
                    }

                    bool hasTranslation = isRawPatchedAll ||
                        (matchedTrans is not null && !string.IsNullOrWhiteSpace(matchedTrans.TranslatedText));

                    if (!hasTranslation && !ShowSystemNoise && entry.IsSystemNoise)
                    {
                        continue;
                    }

                    if (matchedTrans is not null && hasTranslation)
                    {
                        consumedTranslatedItems.Add(matchedTrans);
                    }

                    string displayOriginal = matchedTrans is not null && !string.IsNullOrEmpty(matchedTrans.OriginalText)
                        ? matchedTrans.OriginalText
                        : entry.OriginalText;

                    string translatedText = isRawPatchedAll
                        ? "[Đã thay thế bằng nội dung File Raw]"
                        : (matchedTrans?.TranslatedText ?? string.Empty);

                    _activePatchTargetAllSentences.Add(new PatchSentenceComparisonItem(
                        Id: entry.Id,
                        Key: entry.Key,
                        OriginalText: displayOriginal,
                        TranslatedText: translatedText,
                        Context: entry.Context,
                        IsPatched: hasTranslation,
                        IsAppliedToDisk: targetItem.IsAppliedToDisk));
                }

                // Ensure any translated entries whose binary offsets shifted after disk patching are always included
                int insertIdx = 0;
                foreach (TextEntry unconsumed in allTranslatedItemsInPayload)
                {
                    if (consumedTranslatedItems.Contains(unconsumed))
                    {
                        continue;
                    }

                    _activePatchTargetAllSentences.Insert(insertIdx++, new PatchSentenceComparisonItem(
                        Id: unconsumed.Id,
                        Key: unconsumed.Key,
                        OriginalText: unconsumed.OriginalText,
                        TranslatedText: unconsumed.TranslatedText,
                        Context: unconsumed.Context,
                        IsPatched: true,
                        IsAppliedToDisk: targetItem.IsAppliedToDisk));
                }
            }
            else if (payload is not null)
            {
                foreach (TextEntry tItem in payload.Package.Entries)
                {
                    bool hasTranslation = !string.IsNullOrWhiteSpace(tItem.TranslatedText);

                    _activePatchTargetAllSentences.Add(new PatchSentenceComparisonItem(
                        Id: tItem.Id,
                        Key: tItem.Key,
                        OriginalText: tItem.OriginalText,
                        TranslatedText: tItem.TranslatedText,
                        Context: tItem.Context,
                        IsPatched: hasTranslation,
                        IsAppliedToDisk: targetItem.IsAppliedToDisk));
                }
            }

            int patchedInTarget = _activePatchTargetAllSentences.Count(x => x.IsPatched);
            int unpatchedInTarget = _activePatchTargetAllSentences.Count - patchedInTarget;
            SelectedPatchTargetMeta = $"Nguồn: {targetItem.SourceKind}  •  Đã dịch/vá: {patchedInTarget:N0} câu  •  Chưa vá: {unpatchedInTarget:N0} câu  •  File: {targetItem.ContainerSubtitle}";

            RefreshFilteredPatchSentences();
        }
        catch (Exception ex)
        {
            SelectedPatchTargetMeta = $"Lỗi khi tải bảng đối chiếu câu: {ex.Message}";
        }
        finally
        {
            IsPatchComparisonLoading = false;
        }
    }

    private void RefreshFilteredPatchSentences()
    {
        PatchComparisonEntries.Clear();
        PatchStatusFilter statusFilter = SelectedSentencePatchFilterOption?.Filter ?? PatchStatusFilter.All;
        string q = PatchSearchQuery.Trim();
        bool hasQuery = !string.IsNullOrEmpty(q);

        const int maxRows = 2500;
        int added = 0;

        IEnumerable<PatchSentenceComparisonItem> orderedSource = _activePatchTargetAllSentences
            .OrderByDescending(x => x.IsPatched ? 1 : 0);

        foreach (PatchSentenceComparisonItem item in orderedSource)
        {
            if (statusFilter == PatchStatusFilter.Patched && !item.IsPatched)
            {
                continue;
            }

            if (statusFilter == PatchStatusFilter.Unpatched && item.IsPatched)
            {
                continue;
            }

            if (hasQuery &&
                !item.OriginalText.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.TranslatedText.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.Key.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PatchComparisonEntries.Add(item);
            added++;
            if (added >= maxRows)
            {
                break;
            }
        }
    }

    private static int CountTranslatedEntriesInPayload(PatchPayload payload, int fallbackTotal = 1)
    {
        if (payload.Package.Entries.Count > 0)
        {
            int count = payload.Package.Entries.Count(e =>
                !string.IsNullOrWhiteSpace(e.TranslatedText) &&
                !string.Equals(e.TranslatedText, e.OriginalText, StringComparison.Ordinal));

            return count > 0 ? count : payload.Package.Entries.Count(e => !string.IsNullOrWhiteSpace(e.TranslatedText));
        }

        if (payload.IsRawOverride)
        {
            return Math.Max(1, fallbackTotal);
        }

        return 0;
    }

    private static string GetTargetMatchKey(ScannedTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.TargetId))
        {
            return target.TargetId;
        }

        return $"{target.ContainerRelativePath}|{target.BundleMemberName}|{target.PrimaryPathId}|{target.DisplayName}";
    }

    private static string GetPayloadMatchKey(PatchPayload payload)
    {
        TranslationPackage pkg = payload.Package;
        if (!string.IsNullOrWhiteSpace(pkg.TargetId))
        {
            return pkg.TargetId;
        }

        return $"{pkg.ContainerRelativePath}|{pkg.BundleMemberName}|{pkg.DisplayName}";
    }

    private static PatchPayload? FindMatchingPayload(
        ScannedTarget target,
        Dictionary<string, PatchPayload> payloadByKey,
        out string? matchedKey)
    {
        string primaryKey = GetTargetMatchKey(target);
        if (payloadByKey.TryGetValue(primaryKey, out PatchPayload? directMatch))
        {
            matchedKey = primaryKey;
            return directMatch;
        }

        foreach (var kvp in payloadByKey)
        {
            TranslationPackage pkg = kvp.Value.Package;
            if (!string.IsNullOrWhiteSpace(pkg.TargetId) &&
                string.Equals(pkg.TargetId, target.TargetId, StringComparison.OrdinalIgnoreCase))
            {
                matchedKey = kvp.Key;
                return kvp.Value;
            }

            bool sameContainer = string.Equals(
                pkg.ContainerRelativePath.Replace('\\', '/'),
                target.ContainerRelativePath.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase);

            if (sameContainer && string.Equals(pkg.DisplayName, target.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                matchedKey = kvp.Key;
                return kvp.Value;
            }
        }

        matchedKey = null;
        return null;
    }

    // =========================================================================
    // PAGE 2 HELPERS: FILTERING & PREVIEW LOADING
    // =========================================================================
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
