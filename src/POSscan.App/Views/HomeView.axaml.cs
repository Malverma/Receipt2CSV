using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using POSscan.App.ViewModels;

namespace POSscan.App.Views;

public partial class HomeView : UserControl
{
    private static readonly IBrush Idle = Brush.Parse("#B9C2CF"), Over = Brush.Parse("#0F6CBD");
    private DispatcherTimer? _timer;

    public HomeView()
    {
        InitializeComponent();
        DropZone.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DragDropEffects.Copy; DropZone.BorderBrush = Over; });
        DropZone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.BorderBrush = Idle);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.BorderBrush = Idle;
        var path = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
        if (path is not null && DataContext is HomeVm vm) await vm.DropAsync(path);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += (_, _) => (DataContext as HomeVm)?.CheckScanFolder();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
