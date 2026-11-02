using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using POSscan.App.ViewModels;

namespace POSscan.App.Views;

public partial class ReviewView : UserControl
{
    private ReviewVm? _vm;
    private ManualConfirmVm? _manual;

    public ReviewView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ReviewVm;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        WatchManual();
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
        if (e.PropertyName == nameof(ReviewVm.Manual)) WatchManual();
    }

    private void WatchManual()
    {
        if (_manual is not null) _manual.LineShown -= FocusManualBox;
        _manual = _vm?.Manual;
        if (_manual is not null)
        {
            _manual.LineShown += FocusManualBox;
            FocusManualBox();
        }
    }

    // Posted, because the box has to become visible before it can take the cursor.
    private void FocusManualBox() => Dispatcher.UIThread.Post(() =>
    {
        if (_manual is null) return;
        if (_manual.IsFinished) { ManualDone.Focus(); return; }
        ManualBox.Focus();
        ManualBox.CaretIndex = ManualBox.Text?.Length ?? 0;
    }, DispatcherPriority.Background);

    // Enter confirms the line wherever the cursor is, and Esc closes without saving the line on screen.
    private void OnManualKeyDown(object? sender, KeyEventArgs e)
    {
        if (_manual is null) return;
        if (e.Key is Key.Enter or Key.Return)
        {
            _manual.Next.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _manual.Close.Execute(null);
            e.Handled = true;
        }
    }

    private void ScrollToHighlight()
    {
        if (_vm is not { ShowHighlight: true }) return;
        var y = Math.Max(0, _vm.HighlightTop - ImageScroll.Viewport.Height / 2 + 12);
        ImageScroll.Offset = new Vector(ImageScroll.Offset.X, y);
    }
}
