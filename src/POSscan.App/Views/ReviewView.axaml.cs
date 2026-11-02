using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using POSscan.App.ViewModels;

namespace POSscan.App.Views;

public partial class ReviewView : UserControl
{
    private ReviewVm? _vm;

    public ReviewView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ReviewVm;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // The scroll area has no size until it's laid out, so the first line needs a later pass.
        Dispatcher.UIThread.Post(ScrollToHighlight, DispatcherPriority.Background);
    }

    // Keeps the selected line in view on the receipt image, so it can be compared at a glance.
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReviewVm.HighlightTop)) ScrollToHighlight();
    }

    private void ScrollToHighlight()
    {
        if (_vm is not { ShowHighlight: true }) return;
        var y = Math.Max(0, _vm.HighlightTop - ImageScroll.Viewport.Height / 2 + 12);
        ImageScroll.Offset = new Vector(ImageScroll.Offset.X, y);
    }
}
