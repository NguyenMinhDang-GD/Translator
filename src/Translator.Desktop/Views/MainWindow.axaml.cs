using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Translator.Desktop.ViewModels;

namespace Translator.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnPickGameFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Chọn thư mục gốc của Game Unity",
                AllowMultiple = false
            });

        if (folders.Count > 0)
        {
            string? localPath = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                await vm.ScanGameFolderAsync(localPath);
            }
        }
    }

    private async void OnExtractClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (vm.GetTargetsForExtraction().Count == 0)
        {
            vm.StatusMessage = "Vui lòng chọn thư mục Game và tick ít nhất 1 mục cần Extract.";
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Chọn thư mục trên máy để lưu các file Text được Extract",
                AllowMultiple = false
            });

        if (folders.Count > 0)
        {
            string? outputDir = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                await vm.ExecuteExtractAsync(outputDir);
            }
        }
    }

    private async void OnStagePatchFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Chọn thư mục chứa bản dịch (manifest.project.json / .csv / .translator.json)",
                AllowMultiple = false
            });

        if (folders.Count > 0)
        {
            string? patchDir = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(patchDir))
            {
                await vm.StagePatchFolderAsync(patchDir);
            }
        }
    }

    private async void OnStagePatchFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Chọn một hoặc nhiều file bản dịch (.csv, .translator.json hoặc file Raw)",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("File Bản Dịch (*.csv;*.translator.json;*.raw.*)")
                    {
                        Patterns = ["*.csv", "*.translator.json", "*.raw.*"]
                    },
                    FilePickerFileTypes.All
                ]
            });

        if (files.Count > 0)
        {
            List<string> paths = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Cast<string>()
                .ToList();

            if (paths.Count > 0)
            {
                await vm.StagePatchFilesAsync(paths);
            }
        }
    }

    private async void OnPatchFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (!vm.HasActiveGame)
        {
            vm.StatusMessage = "Vui lòng chọn thư mục Game (ở Trang 1) trước khi Vá bản dịch.";
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Chọn thư mục chứa bản dịch để Vá ngay vào Game",
                AllowMultiple = false
            });

        if (folders.Count > 0)
        {
            string? patchDir = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(patchDir))
            {
                await vm.ExecutePatchFromFolderAsync(patchDir);
            }
        }
    }

    private async void OnPatchFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (!vm.HasActiveGame)
        {
            vm.StatusMessage = "Vui lòng chọn thư mục Game (ở Trang 1) trước khi Vá bản dịch.";
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Chọn file bản dịch lẻ (.csv, .translator.json hoặc file Raw) để Vá ngay",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("File Bản Dịch (*.csv;*.translator.json;*.raw.*)")
                    {
                        Patterns = ["*.csv", "*.translator.json", "*.raw.*"]
                    },
                    FilePickerFileTypes.All
                ]
            });

        if (files.Count > 0)
        {
            string? filePath = files[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                await vm.ExecutePatchFromFileAsync(filePath);
            }
        }
    }
}
