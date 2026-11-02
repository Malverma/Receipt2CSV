using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using POSscan.App.ViewModels;

namespace POSscan.App.Views;

public partial class MainWindow : Window, IShell
{
    public MainWindow() => InitializeComponent();

    public async Task<string?> PickPdfAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a receipt",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF scans") { Patterns = ["*.pdf"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open", $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Open folder", ex);
        }
    }
}
