using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using POSscan.App.ViewModels;
using POSscan.App.Views;
using POSscan.Data;
using POSscan.Ocr;

namespace POSscan.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("UI", e.Exception);
            e.Handled = true; // A bad click shouldn't lose the session. All edits are already saved.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"FATAL {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Task", e.Exception); e.SetObserved(); };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            try
            {
                var store = new PosStore(Paths.DataDir);
                TryBackup(store);
                var reader = new ReceiptReader();
                window.DataContext = new MainVm(new Workflow(store, reader), window);
                desktop.ShutdownRequested += (_, _) => { reader.Dispose(); store.Dispose(); };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Startup", ex);
                window.DataContext = new StartupErrorVm(ex.Message, Log.Folder);
            }
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void TryBackup(PosStore store)
    {
        try { store.BackupIfDue(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error("Backup", ex);
        }
    }
}
