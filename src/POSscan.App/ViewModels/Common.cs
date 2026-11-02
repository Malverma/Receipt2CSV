using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace POSscan.App.ViewModels;

public abstract class Vm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Command(Action run, Func<bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => can?.Invoke() ?? true;
    public void Execute(object? parameter) => run();
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>What the screens need from the window. Replaced by a stub in tests.</summary>
public interface IShell
{
    Task<string?> PickPdfAsync();
    Task<string?> PickFolderAsync(string title);
    void OpenFolder(string path);
}

public sealed record StartupErrorVm(string Message, string LogFolder);
