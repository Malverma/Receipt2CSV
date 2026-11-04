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

    // Dragging the receipt image moves it around; a press that barely moves is a click.
    private const double DragThreshold = 4;
    private Point? _dragStart;
    private Vector _dragOffset;
    private bool _dragged, _anchoring;

    public ReviewView()
    {
        InitializeComponent();
        // Tunnel, so the zoom gets the wheel before the scroll area uses it to scroll.
        ImageScroll.AddHandler(PointerWheelChangedEvent, OnImageWheel, RoutingStrategies.Tunnel);
        ImageScroll.SizeChanged += (_, _) => UpdateFitWidth();
        ImageHost.PointerPressed += OnImagePressed;
        ImageHost.PointerMoved += OnImageMoved;
        ImageHost.PointerReleased += OnImageReleased;
        ImageHost.PointerCaptureLost += (_, _) => _dragStart = null;
        ImageHost.Tapped += OnImageTapped;
        ImageHost.DoubleTapped += OnImageDoubleTapped;
        FitButton.Click += (_, _) => _vm?.SetZoom(1);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ReviewVm;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        UpdateFitWidth();
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
        if (e.PropertyName == nameof(ReviewVm.HighlightTop) && !_anchoring) ScrollToHighlight();
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

    // Handled here, or Enter would also press Confirm in the popup or the main screen's default button.
    private void OnManualSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_manual is null || e.Key is not (Key.Enter or Key.Return)) return;
        _manual.Search.Execute(null);
        e.Handled = true;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Key is not (Key.Enter or Key.Return)) return;
        _vm.Search.Execute(null);
        e.Handled = true;
    }

    // Brings the selected line into view, centred, unless it's already all on screen, so clicking the image doesn't jump it.
    private void ScrollToHighlight()
    {
        if (_vm is not { ShowHighlight: true }) return;
        double top = _vm.HighlightTop + 12, bottom = top + _vm.HighlightHeight, view = ImageScroll.Viewport.Height;
        if (view <= 0 || (top >= ImageScroll.Offset.Y && bottom <= ImageScroll.Offset.Y + view)) return;
        var y = Math.Max(0, top - view / 2);
        ImageScroll.Offset = new Vector(ImageScroll.Offset.X, y);
    }

    // At zoom 1 the receipt fills its column, whatever width the column has been dragged to.
    private void UpdateFitWidth()
    {
        if (_vm is null || ImageScroll.Bounds.Width <= 0) return;
        _vm.FitWidth = ImageScroll.Bounds.Width - 24;
    }

    private void OnImageWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_vm is null || e.Delta.Y == 0) return;
        ZoomAt(_vm.Zoom * Math.Pow(1.2, e.Delta.Y), e.GetPosition(ImageScroll));
        e.Handled = true;
    }

    private void OnImageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null) return;
        ZoomAt(_vm.Zoom < 1.75 ? 2.5 : 1, e.GetPosition(ImageScroll));
        e.Handled = true;
    }

    /// <summary>Zooms so the spot under <paramref name="pointer"/> (in the scroll area) stays under the pointer.</summary>
    private void ZoomAt(double zoom, Point pointer)
    {
        if (_vm is null || ImageScroll.TranslatePoint(pointer, ImageHost) is not { } onImage) return;
        double before = _vm.ImageWidth;
        _anchoring = true;
        try
        {
            _vm.SetZoom(zoom);
            ImageScroll.UpdateLayout();
        }
        finally { _anchoring = false; }
        double k = before > 0 ? _vm.ImageWidth / before : 1;
        if (ImageHost.TranslatePoint(new Point(onImage.X * k, onImage.Y * k), ImageScroll) is { } now)
            ImageScroll.Offset += now - pointer;
    }

    private void OnImagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ImageHost).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(ImageScroll);
        _dragOffset = ImageScroll.Offset;
        _dragged = false;
        e.Pointer.Capture(ImageHost);
    }

    private void OnImageMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start) return;
        var moved = e.GetPosition(ImageScroll) - start;
        if (!_dragged && Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold) return;
        _dragged = true;
        ImageHost.Cursor = new Cursor(StandardCursorType.SizeAll);
        ImageScroll.Offset = _dragOffset - moved;
    }

    private void OnImageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        ImageHost.Cursor = new Cursor(StandardCursorType.Hand);
        e.Pointer.Capture(null);
    }

    // Clicking a line on the receipt selects its row in the list, which also outlines it on the image.
    private void OnImageTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || _dragged) return;
        _vm.SelectAtImagePoint(e.GetPosition(ImageHost).Y);
    }
}
