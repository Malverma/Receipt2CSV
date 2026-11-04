using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using POSscan.Core;
using POSscan.Data;

namespace POSscan.App.ViewModels;

/// <summary>The popup that goes down a receipt line by line. Each Enter saves that line straight away.</summary>
public sealed class ManualConfirmVm : Vm
{
    private readonly ReceiptSession _session;
    private readonly Bitmap? _preview;
    private readonly Action _close;
    private readonly List<ConfirmStep> _steps;
    private int _index;
    private string _text = "";
    private string? _error;
    private IImage? _clip;
    private string _search = "";

    public string Title => IsFinished ? "All lines checked" : ManualConfirmation.Title(_session.Receipt, Step);
    public string Progress => IsFinished ? "" : $"Line {_index + 1} of {_steps.Count}";
    public string Hint => IsFinished ? "" : ManualConfirmation.Hint(_session.Receipt, Step);
    public bool HasHint => Hint.Length > 0;
    public string? Warning
    {
        get
        {
            if (IsFinished) return null;
            var total = ManualConfirmation.Warning(_session.Receipt, Step);
            if (total is not null || Step.Kind != ConfirmKind.Item) return total;
            var near = _session.NearMisses(Step.Line);
            return near.Count == 0 ? null : $"This code is one digit away from an item you've already set up, so it may be misread. Check it against the receipt. Similar: {string.Join(", ", near.Select(n => n.Replace("Use ", "")))}.";
        }
    }
    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public string Text { get => _text; set => Set(ref _text, value); }
    public string SearchText { get => _search; set => Set(ref _search, value); }
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);
    public IImage? Clip { get => _clip; private set { if (Set(ref _clip, value)) Raise(nameof(HasClip)); } }
    public bool HasClip => _clip is not null;
    public bool IsFinished => _index >= _steps.Count;
    public bool IsWorking => !IsFinished;
    public string FinishText
    {
        get
        {
            var r = _session.Review;
            var totals = r.TotalsMatch ? $"The totals match at {_session.Receipt.Subtotal:0.00}." : "The totals still don't match.";
            return r.AttentionCount == 0 ? $"{totals} Nothing else needs checking."
                : r.AttentionCount == 1 ? $"{totals} 1 thing still needs checking on the main screen."
                : $"{totals} {r.AttentionCount} things still need checking on the main screen.";
        }
    }

    public Command Next { get; }
    public Command Previous { get; }
    public Command SkipStep { get; }
    public Command Close { get; }
    public Command Search { get; }
    public Command CreateFile { get; }

    /// <summary>Raised when the box shows a new line, so the view can put the cursor in it.</summary>
    public event Action? LineShown;

    public ManualConfirmVm(ReceiptSession session, Bitmap? preview, Action close, Action createFile, Func<bool> canCreateFile)
    {
        _session = session;
        _preview = preview;
        _close = close;
        _steps = ManualConfirmation.Steps(session.Receipt, session.Decisions);
        Next = new Command(DoNext);
        Previous = new Command(() => Show(_index - 1), () => _index > 0);
        SkipStep = new Command(() => Show(_index + 1));
        Close = new Command(close);
        Search = new Command(DoSearch);
        CreateFile = new Command(createFile, canCreateFile);
        Show(0);
    }

    private ConfirmStep Step => _steps[_index];

    private void DoNext()
    {
        if (IsFinished) { _close(); return; }
        Error = _session.ConfirmStep(Step, Text);
        if (Error is null) Show(_index + 1);
    }

    // Goes to the next item that fits, so pressing Search again moves on to the one after it. The line on screen isn't saved.
    private void DoSearch()
    {
        var search = SearchText.Trim();
        if (search.Length == 0) return;
        for (int offset = 1; offset <= _steps.Count; offset++)
        {
            int index = (Math.Min(_index, _steps.Count - 1) + offset) % _steps.Count;
            if (_steps[index].Kind != ConfirmKind.Item) continue;
            var line = _session.Review.Lines.FirstOrDefault(l => l.Item.SourceLine == _steps[index].Line);
            if (line is null || !Review.Matches(line, search)) continue;
            Show(index);
            return;
        }
        Error = $"No item on this receipt matches \"{search}\".";
    }

    private void Show(int index)
    {
        _index = Math.Clamp(index, 0, _steps.Count);
        Error = null;
        if (IsFinished) _session.MarkManualConfirmationDone();
        Text = IsFinished ? "" : _session.FormatStep(Step);
        Clip = IsFinished ? null : MakeClip();
        foreach (var p in new[] { nameof(Title), nameof(Progress), nameof(Hint), nameof(HasHint), nameof(Warning), nameof(HasWarning), nameof(IsFinished), nameof(IsWorking), nameof(FinishText) }) Raise(p);
        Previous.Refresh();
        CreateFile.Refresh();
        LineShown?.Invoke();
    }

    // The part of the scan this line came from, so the numbers can be compared without looking away from the box.
    private IImage? MakeClip()
    {
        if (_preview is null || _session.Record.PageWidth <= 0) return null;
        var lines = _session.Record.Lines;
        var (first, last) = Span();
        if (first < 0 || last >= lines.Count) return null;

        const int margin = 8;
        double scale = (double)_preview.PixelSize.Width / _session.Record.PageWidth;
        int top = Math.Max(0, (int)((lines[first].Top - margin) * scale));
        int bottom = Math.Min(_preview.PixelSize.Height, (int)((lines[last].Bottom + margin) * scale));
        // Trim to the printed width. A scan can have a wide empty area beside the receipt.
        var printed = lines.Where(l => l.Right > l.Left).ToList();
        int left = printed.Count == 0 ? 0 : Math.Max(0, (int)((printed.Min(l => l.Left) - margin) * scale));
        int right = printed.Count == 0 ? _preview.PixelSize.Width : Math.Min(_preview.PixelSize.Width, (int)((printed.Max(l => l.Right) + margin) * scale));
        if (bottom <= top || right <= left) return null;
        return new CroppedBitmap(_preview, new PixelRect(left, top, right - left, bottom - top));
    }

    private (int First, int Last) Span()
    {
        var lines = _session.Record.Lines;
        if (Step.Line < 0)
        {
            int subtotal = lines.ToList().FindIndex(l => SuperstoreParser.IsSubtotal(l.Text));
            return subtotal < 0 ? (-1, -1) : (subtotal, Math.Min(subtotal + 3, lines.Count - 1));
        }
        if (Step.Kind == ConfirmKind.GroupTotal) return (Math.Max(0, Step.Line - 1), Step.Line);
        // An item runs down to the next item, department, or total, within reason.
        var later = _session.Receipt.Items.Select(i => i.SourceLine).Where(l => l > Step.Line).DefaultIfEmpty(lines.Count).Min();
        int end = Math.Min(Math.Min(later - 1, Step.Line + 2), lines.Count - 1);
        return (Step.Line, Math.Max(Step.Line, end));
    }
}
