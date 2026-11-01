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
}
