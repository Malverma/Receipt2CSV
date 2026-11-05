using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using POSscan.Core;
using POSscan.Data;

namespace POSscan.App.ViewModels;

public enum RowKind { Totals, Unreadable, Item }

public sealed class FixVm(string label, Command run)
{
    public string Label { get; } = label;
    public Command Run { get; } = run;
}

public sealed record RowVm(
    RowKind Kind, int Line, string Title, string Subtitle, string Amount, string Status, IBrush StatusFg, IBrush StatusBg, ReviewLine? Review)
{
    public string Code { get; init; } = "";
    /// <summary>The number under the product's barcode, blank when there isn't one (in-store and produce codes).</summary>
    public string Barcode { get; init; } = "";
    public string Quantity { get; init; } = "";
    public bool HasCode => Code.Length > 0;
    public string CodeLine => Barcode.Length > 0 ? $"{Code}  ·  Barcode {Barcode}" : Code;
    public bool HasSubtitle => Subtitle.Length > 0;
}

public sealed class ReviewVm : Vm
{
    private const int TotalsLine = -100;
    private const double MinZoom = 1, MaxZoom = 6;

    private static readonly IBrush WarnFg = Brush.Parse("#8A4B00"), WarnBg = Brush.Parse("#FFF4DC");
    private static readonly IBrush GoodFg = Brush.Parse("#0B6B3A"), GoodBg = Brush.Parse("#E3F6EA");
    private static readonly IBrush PlainFg = Brush.Parse("#5D6778"), PlainBg = Brush.Parse("#EEF0F3");
    private static readonly IBrush InfoFg = Brush.Parse("#0F6CBD"), InfoBg = Brush.Parse("#EBF3FC");

    private readonly MainVm _main;
    public ReceiptSession Session { get; }

    private ManualConfirmVm? _manual;
    private RowVm? _selected;
    private bool _isRebuilding, _discardArmed, _exportArmed;
    private string _searchText = "", _appliedSearch = "";
    private double _zoom = 1, _fitWidth = 396;
    private string? _detailError, _pageError;
    private string _code = "", _qty = "", _unit = "", _total = "", _group = "", _name = "", _sku = "", _upc = "", _listing = "", _container = "";
    private string _subtotal = "", _tax = "", _grandTotal = "", _count = "", _force = "";

    public ObservableCollection<RowVm> Rows { get; } = [];
    public ObservableCollection<string> Reasons { get; } = [];
    public ObservableCollection<FixVm> Fixes { get; } = [];
    public Bitmap? Preview { get; }

    /// <summary>The toggle. Remembered between receipts, so it stays on until someone turns it off.</summary>
    public bool ManualConfirmation
    {
        get => _main.Workflow.Store.GetSettings().ManualConfirmation;
        set
        {
            var store = _main.Workflow.Store;
            if (store.GetSettings().ManualConfirmation == value) return;
            store.SaveSettings(store.GetSettings() with { ManualConfirmation = value });
            Raise();
            if (value) OpenManual(); else CloseManual();
        }
    }

    public ManualConfirmVm? Manual { get => _manual; private set { if (Set(ref _manual, value)) Raise(nameof(IsManualOpen)); Raise(nameof(IsManualClosed)); } }
    public bool IsManualOpen => _manual is not null;
    public bool IsManualClosed => _manual is null;

    public string HeaderTitle => Session.Record.Supplier;
    public string HeaderDate => (Session.Receipt.Date ?? Session.Record.ReceiptDate)?.ToString("MMMM d, yyyy") ?? "Date not read";

    public string TotalsChip => Session.Review.TotalsMatch
        ? $"Totals match: {Session.Receipt.Subtotal:0.00}"
        : Session.Review.Forced ? "Totals don't match (override)" : Session.Check.CalculatedSubtotal is { } c && Session.Receipt.Subtotal is { } s
            ? $"Totals off by {Math.Abs(s - c):0.00}" : "Totals not read";
    public IBrush TotalsFg => Session.Review.TotalsMatch ? GoodFg : WarnFg;
    public IBrush TotalsBg => Session.Review.TotalsMatch ? GoodBg : WarnBg;

    public string FooterText
    {
        get
        {
            var r = Session.Review;
            if (_exportArmed) return EarlyFileText();
            if (r.AttentionCount > 0 && !r.CanExportUnchecked) return r.AttentionCount == 1 ? "1 thing to check before you can create the file" : $"{r.AttentionCount} things to check before you can create the file";
            if (r.AttentionCount > 0) return r.AttentionCount == 1 ? "1 thing still to check, or create the file now" : $"{r.AttentionCount} things still to check, or create the file now";
            int n = r.ToExport.Count();
            return n == 0 ? "No costs have changed, so there's nothing to send to Estream." : n == 1 ? "1 item is ready to update in Estream" : $"{n} items are ready to update in Estream";
        }
    }

    public bool CanExport => Session.Review.CanExport;
    public bool CanCreateFile => Session.Review.CanExportUnchecked;
    public string ExportLabel => _exportArmed ? "Create file anyway" : "Create price file";

    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public bool HasSearch => _appliedSearch.Length > 0;
    public bool HasNoResults => HasSearch && Rows.Count == 0;
    public string NoResultsText => $"No items match \"{_appliedSearch}\".";
    public string? PageError { get => _pageError; private set { if (Set(ref _pageError, value)) Raise(nameof(HasPageError)); } }
    public bool HasPageError => !string.IsNullOrEmpty(_pageError);

    public RowVm? Selected
    {
        get => _selected;
        set
        {
            if (value is null && _isRebuilding) return;
            if (Set(ref _selected, value)) LoadDetail();
        }
    }

    // ---- Which parts of the panel are showing ----
    public bool HasSelection => _selected is not null;
    public bool IsItem => _selected?.Kind == RowKind.Item;
    public bool IsTotals => _selected?.Kind == RowKind.Totals;
    public bool IsUnreadable => _selected?.Kind == RowKind.Unreadable;
    public bool ShowsCodeAndPrice => IsItem || IsUnreadable;
    public bool ShowsName => ShowsMapping || IsUnreadable;
    public bool IsGroup => _selected?.Review?.Item.GroupId is not null;
    public bool IsSingleAmount => IsItem && !IsGroup;
    public bool ShowsLineTotal => IsItem && !IsGroup && (_selected?.Review?.Item.Quantity != 1m || _selected?.Review?.Item.Unit == PriceUnit.Kg);
    public bool ShowsMapping => IsItem
        && (_selected?.Review?.Item.Unit != PriceUnit.Kg || _main.Workflow.Store.GetSettings().ExportWeighed)
        && _selected?.Review?.State is not (LineState.Skipped or LineState.NotForResale);
    public bool IsSkipped => _selected?.Review?.State == LineState.Skipped;
    public bool IsNotForResale => _selected?.Review?.State == LineState.NotForResale;
    public bool ShowsActions => IsItem && !IsSkipped && !IsNotForResale;
    public bool HasFixes => Fixes.Count > 0;
    public bool HasReasons => Reasons.Count > 0;
    public bool CanForce => IsTotals && !Session.Review.TotalsMatch;
    public string QuantityLabel => _selected?.Review?.Item.Unit == PriceUnit.Kg ? "Weight (kg)" : "Quantity";
    public string PriceLabel => _selected?.Review?.Item.Unit == PriceUnit.Kg ? "Price per kg" : "New unit cost";
    public string DetailTitle => _selected?.Title ?? "";
    public string DetailBarcode => IsItem ? _selected!.Barcode : "";
    public bool HasDetailBarcode => DetailBarcode.Length > 0;
    public string BarcodeNote => Upc.Barcode("", _selected?.Review?.Mapping?.Upc) is not null
        ? "From the saved item's UPC."
        : "Worked out from the receipt code by adding its check digit. Compare it with the barcode on the product.";
    public string CostSummary => BuildCostSummary();
    public string ConfirmLabel => IsTotals ? "Save totals" : IsUnreadable ? "Add as item" : _selected?.Review?.State == LineState.Attention ? "Confirm" : "Save";
    public string? DetailError { get => _detailError; private set { if (Set(ref _detailError, value)) Raise(nameof(HasDetailError)); } }
    public bool HasDetailError => !string.IsNullOrEmpty(_detailError);

    public string CodeText { get => _code; set => Set(ref _code, value); }
    public string QuantityText { get => _qty; set => Set(ref _qty, value); }
    public string UnitText { get => _unit; set => Set(ref _unit, value); }
    public string TotalText { get => _total; set => Set(ref _total, value); }
    public string GroupText { get => _group; set => Set(ref _group, value); }
    public string NameText { get => _name; set => Set(ref _name, value); }
    public string SkuText { get => _sku; set => Set(ref _sku, value); }
    public string UpcText { get => _upc; set => Set(ref _upc, value); }
    public string ListingText { get => _listing; set => Set(ref _listing, value); }
    public string ContainerText { get => _container; set => Set(ref _container, value); }
    public string SubtotalText { get => _subtotal; set => Set(ref _subtotal, value); }
    public string TaxText { get => _tax; set => Set(ref _tax, value); }
    public string GrandTotalText { get => _grandTotal; set => Set(ref _grandTotal, value); }
    public string CountText { get => _count; set => Set(ref _count, value); }
    public string ForceText { get => _force; set => Set(ref _force, value); }

    // ---- Receipt image ----
    /// <summary>The width that fills the receipt column, set by the view as the column is resized. Zoom 1 is this width.</summary>
    public double FitWidth
    {
        get => _fitWidth;
        set
        {
            value = Math.Max(120, value);
            if (Math.Abs(value - _fitWidth) < 0.5) return;
            _fitWidth = value;
            RaiseImageSize();
        }
    }
    public double Zoom => _zoom;
    public double ImageWidth => _fitWidth * _zoom;
    public double ImageHeight => Session.Record.PageWidth > 0 ? ImageWidth * Session.Record.PageHeight / Session.Record.PageWidth : 0;
    private double Scale => Session.Record.PageWidth > 0 ? ImageWidth / Session.Record.PageWidth : 0;
    public double HighlightTop => HighlightSpan().Top * Scale - 3;
    public double HighlightHeight => Math.Max(0, HighlightSpan().Height * Scale + 6);
    public bool ShowHighlight => HighlightSpan().Height > 0;

    public Command Back { get; }
    public Command Confirm { get; }
    public Command SkipLine { get; }
    public Command RestoreLine { get; }
    public Command NotForResale { get; }
    public Command RestoreResale { get; }
    public Command IgnoreUnreadable { get; }
    public Command ForceExport { get; }
    public Command Export { get; }
    public Command Discard { get; }
    public Command ZoomIn { get; }
    public Command ZoomOut { get; }
    public Command Search { get; }
    public Command ClearSearch { get; }
    public string DiscardLabel => _discardArmed ? "Click again to discard" : "Discard receipt";

    public ReviewVm(MainVm main, ReceiptSession session)
    {
        _main = main;
        Session = session;
        try { if (File.Exists(session.Record.PreviewPath)) Preview = new Bitmap(session.Record.PreviewPath); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { Log.Error("Preview", ex); }

        Back = new Command(() => main.ShowHome());
        Confirm = new Command(DoConfirm);
        SkipLine = new Command(() => ApplyToSelected(new ReceiptEdit(EditKind.Skip, _selected!.Line)));
        RestoreLine = new Command(() => ApplyToSelected(new ReceiptEdit(EditKind.Unskip, _selected!.Line)));
        NotForResale = new Command(DoNotForResale);
        RestoreResale = new Command(() => { Session.RemoveMapping(_selected!.Review!.Item.RawCode); Refresh(_selected.Line); });
        IgnoreUnreadable = new Command(() => ApplyToSelected(new ReceiptEdit(EditKind.IgnoreLine, _selected!.Line), moveOn: true));
        ForceExport = new Command(DoForce);
        Export = new Command(DoExport, () => CanCreateFile);
        Search = new Command(() => ApplySearch(SearchText));
        ClearSearch = new Command(() => { SearchText = ""; ApplySearch(""); });
        Discard = new Command(DoDiscard);
        ZoomIn = new Command(() => SetZoom(_zoom * 1.5));
        ZoomOut = new Command(() => SetZoom(_zoom / 1.5));
        Refresh(null, selectFirstAttention: true);
        if (ManualConfirmation && !Session.ManualConfirmationDone) OpenManual();
    }

    private void OpenManual() => Manual = new ManualConfirmVm(Session, Preview, CloseManual, () =>
    {
        CloseManual();
        DoExport();
    }, () => CanCreateFile);

    private void ApplySearch(string search)
    {
        _appliedSearch = search.Trim();
        Refresh(null, selectFirstAttention: true);
    }

    // The search looks at what's on the row, so it also finds lines that couldn't be read.
    private bool FitsSearch(RowVm row) => !HasSearch
        || (row.Review is { } line ? Core.Review.Matches(line, _appliedSearch)
            : row.Kind == RowKind.Unreadable && Core.Review.MatchesText(_appliedSearch, row.Title, row.Subtitle));

    private string EarlyFileText()
    {
        var r = Session.Review;
        var parts = new List<string>();
        int inFile = r.Unchecked.Count(), leftOut = r.LeftOut.Count() + r.Unreadable.Count;
        if (inFile > 0) parts.Add(inFile == 1 ? "1 unchecked item goes in as read." : $"{inFile} unchecked items go in as read.");
        if (leftOut > 0) parts.Add(leftOut == 1 ? "1 line is left out." : $"{leftOut} lines are left out.");
        if (!r.TotalsMatch && !r.Forced) parts.Add("The totals aren't matched.");
        parts.Add("Click again to create the file.");
        return string.Join(" ", parts);
    }

    private void CloseManual()
    {
        if (_manual is null) return;
        Manual = null;
        Refresh(null, selectFirstAttention: true);
    }

    public void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 0.001) return;
        _zoom = zoom;
        RaiseImageSize();
    }

    private void RaiseImageSize()
    {
        Raise(nameof(Zoom));
        Raise(nameof(ImageWidth));
        Raise(nameof(ImageHeight));
        RaiseHighlight();
    }

    /// <summary>
    /// Selects the row for whatever was clicked on the receipt image, <paramref name="y"/> being measured down the image
    /// as shown. Returns false when the click isn't on anything that has a row, such as the store's address.
    /// </summary>
    public bool SelectAtImagePoint(double y)
    {
        if (Scale <= 0) return false;
        var lines = Session.Record.Lines;
        double pageY = y / Scale;
        int hit = -1;
        double best = double.MaxValue;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Bottom <= lines[i].Top) continue;
            // Inside a line wins outright; otherwise the nearest one, if the click landed in the gap between two.
            double gap = pageY < lines[i].Top ? lines[i].Top - pageY : pageY > lines[i].Bottom ? pageY - lines[i].Bottom : 0;
            if (gap < best) { best = gap; hit = i; }
        }
        if (hit < 0 || best > (lines[hit].Bottom - lines[hit].Top)) return false;

        var row = RowForLine(hit);
        if (row is null) return false;
        if (!Rows.Contains(row))
        {
            // The search is hiding it, so drop the search rather than ignore the click.
            SearchText = "";
            _appliedSearch = "";
            Refresh(row.Line);
            row = Rows.FirstOrDefault(r => r.Line == row.Line && r.Kind == row.Kind);
        }
        else Selected = row;
        return row is not null;
    }

    private RowVm? RowForLine(int line)
    {
        var all = AllRows();
        if (all.FirstOrDefault(r => r.Kind == RowKind.Unreadable && r.Line == line) is { } unreadable) return unreadable;
        int subtotal = SubtotalLineIndex();
        if (subtotal >= 0 && line >= subtotal) return all.FirstOrDefault(r => r.Kind == RowKind.Totals);
        // The item that starts at or above the click, as long as its block reaches down to it.
        var owner = all.Where(r => r.Kind == RowKind.Item && r.Line <= line).MaxBy(r => r.Line);
        return owner is not null && BlockEnd(owner.Line) >= line ? owner : null;
    }

    // The rows as shown, plus any the search is hiding.
    private IEnumerable<RowVm> AllRows()
    {
        foreach (var r in Rows) yield return r;
        if (!HasSearch) yield break;
        foreach (var (line, text) in Session.Review.Unreadable)
            yield return Rows.FirstOrDefault(r => r.Kind == RowKind.Unreadable && r.Line == line)
                ?? new(RowKind.Unreadable, line, "Line couldn't be read", text, "", "Check", WarnFg, WarnBg, null);
        foreach (var l in Session.Review.Lines)
            yield return Rows.FirstOrDefault(r => r.Kind == RowKind.Item && r.Line == l.Item.SourceLine) ?? ToRow(l);
        yield return Rows.FirstOrDefault(r => r.Kind == RowKind.Totals)
            ?? new(RowKind.Totals, TotalsLine, "Receipt totals", "", "", "", PlainFg, PlainBg, null);
    }

    private int SubtotalLineIndex()
    {
        var lines = Session.Record.Lines;
        for (int i = 0; i < lines.Count; i++)
            if (SuperstoreParser.IsSubtotal(lines[i].Text)) return i;
        return -1;
    }

    /// <summary>
    /// The last receipt line that belongs to the item starting at <paramref name="first"/>: its quantity, deposit and
    /// recycling rows, up to the next item, department heading, unreadable line or the subtotal.
    /// </summary>
    private int BlockEnd(int first)
    {
        var lines = Session.Record.Lines;
        var starts = Session.Review.Lines.Select(l => l.Item.SourceLine).Concat(Session.Review.Unreadable.Select(u => u.Line)).ToHashSet();
        int subtotal = SubtotalLineIndex();
        int last = first;
        for (int i = first + 1; i < lines.Count && i <= first + 8; i++)
        {
            if (starts.Contains(i) || i == subtotal || SuperstoreParser.IsDepartment(lines[i].Text)) break;
            if (lines[i].Text.Trim().Length > 0) last = i;
        }
        return last;
    }

    private void RaiseHighlight()
    {
        Raise(nameof(HighlightTop));
        Raise(nameof(HighlightHeight));
        Raise(nameof(ShowHighlight));
    }

    private (double Top, double Height) HighlightSpan()
    {
        if (_selected is null) return (0, 0);
        var lines = Session.Record.Lines;
        if (_selected.Kind == RowKind.Totals)
        {
            var subtotal = lines.FirstOrDefault(l => l.Text.StartsWith("SUB", StringComparison.OrdinalIgnoreCase));
            return subtotal is null ? (0, 0) : (subtotal.Top, subtotal.Bottom - subtotal.Top);
        }
        int first = _selected.Line;
        if (first < 0 || first >= lines.Count) return (0, 0);
        int last = _selected.Kind == RowKind.Item ? BlockEnd(first) : first;
        return (lines[first].Top, lines[last].Bottom - lines[first].Top);
    }

    private string BuildCostSummary()
    {
        if (_selected?.Review is not { } r || r.Cost is not { } cost) return "";
        var unit = r.Item.Unit == PriceUnit.Kg ? " per kg" : " each";
        if (r.OldCost is { } old && old != cost) return $"Cost {cost:0.00}{unit}, was {old:0.00} ({cost - old:+0.00;-0.00})";
        if (r.OldCost is not null) return $"Cost {cost:0.00}{unit}, no change";
        return $"Cost {cost:0.00}{unit}. This is the first cost POSscan has for it.";
    }

    private void Refresh(int? keepLine, bool selectFirstAttention = false)
    {
        _isRebuilding = true;
        _exportArmed = false;
        var review = Session.Review;
        Rows.Clear();
        var attention = new List<RowVm>();
        var totals = new RowVm(RowKind.Totals, TotalsLine, "Receipt totals", TotalsSubtitle(),
            Session.Receipt.Subtotal is { } st ? $"{st:0.00}" : "",
            review.TotalsMatch ? "Match" : review.Forced ? "Override" : "Check",
            review.TotalsMatch ? GoodFg : WarnFg, review.TotalsMatch ? GoodBg : WarnBg, null);
        foreach (var (line, text) in review.Unreadable)
            attention.Add(new(RowKind.Unreadable, line, "Line couldn't be read", text, "", "Check", WarnFg, WarnBg, null));

        var rows = review.Lines.Select(ToRow).ToList();
        var ordered = new List<RowVm>();
        if (!review.TotalsMatch && !review.Forced) ordered.Add(totals);
        ordered.AddRange(attention);
        ordered.AddRange(rows.Where(r => r.Review!.State == LineState.Attention));
        ordered.AddRange(rows.Where(r => r.Review!.State == LineState.Ready));
        ordered.AddRange(rows.Where(r => r.Review!.State == LineState.ByHand));
        ordered.AddRange(rows.Where(r => r.Review!.State == LineState.NoChange));
        ordered.AddRange(rows.Where(r => r.Review!.State is LineState.Skipped or LineState.NotForResale));
        if (review.TotalsMatch || review.Forced) ordered.Add(totals);
        foreach (var r in ordered.Where(FitsSearch)) Rows.Add(r);
        _isRebuilding = false;

        RowVm? next = keepLine is { } k ? Rows.FirstOrDefault(r => r.Line == k) : null;
        if (selectFirstAttention || next is null) next = Rows.FirstOrDefault(NeedsAttention) ?? next ?? Rows.FirstOrDefault();
        _selected = null;
        Selected = next;
        if (next is null) LoadDetail(); // Setting null over null doesn't load, and a search can leave no rows.
        Raise(nameof(Selected));

        foreach (var p in new[] { nameof(TotalsChip), nameof(TotalsFg), nameof(TotalsBg), nameof(FooterText), nameof(CanExport), nameof(CanCreateFile), nameof(ExportLabel), nameof(HeaderDate), nameof(HasSearch), nameof(HasNoResults), nameof(NoResultsText) }) Raise(p);
        Export.Refresh();
    }

    private static bool NeedsAttention(RowVm r) =>
        r.Status == "Check" || r.Review?.State == LineState.Attention;

    private string TotalsSubtitle() =>
        Session.Check.CalculatedSubtotal is { } c && Session.Receipt.Subtotal is { } s && c != s
            ? $"Lines add up to {c:0.00}, the receipt says {s:0.00}" : $"{Session.Check.CalculatedItemCount} items";

    private RowVm ToRow(ReviewLine l)
    {
        var (status, fg, bg) = l.State switch
        {
            LineState.Attention => ("Check", WarnFg, WarnBg),
            LineState.Ready or LineState.NoChange when l.IsChecked => ("Checked", GoodFg, GoodBg),
            LineState.Ready => ("Ready", GoodFg, GoodBg),
            LineState.ByHand => ("By hand", InfoFg, InfoBg),
            LineState.NoChange => ("No change", PlainFg, PlainBg),
            LineState.Skipped => ("Skipped", PlainFg, PlainBg),
            _ => ("Not for resale", PlainFg, PlainBg),
        };
        var qty = l.Item.Unit == PriceUnit.Kg ? $"{l.Item.Quantity:0.###} kg" : $"{l.Item.Quantity:0.###}";
        var amount = l.Cost is { } c ? (l.Item.Unit == PriceUnit.Kg ? $"{c:0.00}/kg" : $"{c:0.00}") : "?";
        var title = l.DisplayName.Length > 0 ? l.DisplayName : "(no name)";
        return new(RowKind.Item, l.Item.SourceLine, title, "", amount, status, fg, bg, l)
        {
            Code = l.Item.RawCode, Barcode = Upc.Barcode(l.Item.RawCode, l.Mapping?.Upc) ?? "", Quantity = qty,
        };
    }

    private void LoadDetail()
    {
        Reasons.Clear();
        Fixes.Clear();
        DetailError = null;
        var row = _selected;
        if (row is not null)
        {
            if (row.Kind == RowKind.Totals)
            {
                foreach (var p in Session.Review.ReceiptProblems) Reasons.Add(p);
                foreach (var n in Session.Review.ReceiptNotes) Reasons.Add(n);
                foreach (var f in Session.Review.ReceiptFixes) Fixes.Add(MakeFix(f, TotalsLine));
                SubtotalText = Fmt(Session.Receipt.Subtotal);
                TaxText = Fmt(Session.Receipt.Tax);
                GrandTotalText = Fmt(Session.Receipt.Total);
                CountText = Session.Receipt.ItemCount?.ToString(CultureInfo.InvariantCulture) ?? "";
                ForceText = Session.Decisions.ForceReason ?? "";
            }
            else if (row.Kind == RowKind.Unreadable)
            {
                Reasons.Add($"The receipt shows: \"{row.Subtitle}\". If this is a real item, add it. If it's not, ignore it.");
                CodeText = NameText = UnitText = "";
                QuantityText = "1";
            }
            else if (row.Review is { } l)
            {
                foreach (var reason in l.Reasons) Reasons.Add(reason);
                foreach (var f in l.Fixes) Fixes.Add(MakeFix(f, l.Item.SourceLine));
                CodeText = l.Item.RawCode;
                QuantityText = l.Item.Quantity.ToString("0.###", CultureInfo.InvariantCulture);
                UnitText = Fmt(l.Item.UnitPrice);
                TotalText = Fmt(l.Item.LineTotal);
                GroupText = l.Item.GroupId is { } id ? Fmt(Session.Receipt.Groups.First(g => g.Id == id).Total) : "";
                var m = l.Mapping ?? Core.Review.NewMappingDraft(l.Item);
                NameText = m.EstreamName;
                SkuText = m.Sku;
                UpcText = m.Upc;
                ListingText = m.ListingCode;
                ContainerText = m.Container;
            }
        }
        foreach (var p in new[]
        {
            nameof(HasSelection), nameof(IsItem), nameof(IsTotals), nameof(IsUnreadable), nameof(ShowsCodeAndPrice), nameof(ShowsName), nameof(IsGroup), nameof(IsSingleAmount),
            nameof(ShowsLineTotal), nameof(ShowsMapping), nameof(IsSkipped), nameof(IsNotForResale), nameof(ShowsActions), nameof(HasFixes), nameof(HasReasons),
            nameof(CanForce), nameof(QuantityLabel), nameof(PriceLabel), nameof(DetailTitle), nameof(CostSummary), nameof(ConfirmLabel),
            nameof(DetailBarcode), nameof(HasDetailBarcode), nameof(BarcodeNote),
        }) Raise(p);
        RaiseHighlight();
    }

    private FixVm MakeFix(SuggestedFix fix, int line) => new(fix.Label, new Command(() =>
    {
        Session.Apply(fix.Edit);
        Refresh(line);
    }));

    private void ApplyToSelected(ReceiptEdit edit, bool moveOn = false)
    {
        Session.Apply(edit);
        Refresh(moveOn ? null : _selected?.Line, selectFirstAttention: moveOn);
    }

    private void DoConfirm()
    {
        DetailError = null;
        if (_selected is not { } row) return;
        switch (row.Kind)
        {
            case RowKind.Totals: SaveTotals(); break;
            case RowKind.Unreadable: AddUnreadableAsItem(row); break;
            default: SaveItem(row); break;
        }
    }

    private void SaveItem(RowVm row)
    {
        var l = row.Review!;
        var item = l.Item;
        var code = CodeText.Trim();
        int line = row.Line;

        var edits = new List<ReceiptEdit>();
        if (code != item.RawCode) edits.Add(new(EditKind.SetCode, line, code));
        if (Changed(QuantityText, item.Quantity, out var qty, "quantity")) edits.Add(new(EditKind.SetQuantity, line, Str(qty)));
        if (item.GroupId is { } id)
        {
            var group = Session.Receipt.Groups.First(g => g.Id == id);
            if (Changed(GroupText, group.Total, out var total, "multi-buy total")) edits.Add(new(EditKind.SetGroupTotal, group.SourceLine, Str(total)));
        }
        else
        {
            if (Changed(UnitText, item.UnitPrice, out var unit, "new unit cost")) edits.Add(new(EditKind.SetUnitPrice, line, Str(unit)));
            if (ShowsLineTotal && Changed(TotalText, item.LineTotal, out var lt, "line total")) edits.Add(new(EditKind.SetLineTotal, line, Str(lt)));
        }
        if (HasDetailError) return;

        bool needsMapping = ShowsMapping;
        var upc = UpcText.Trim();
        if (needsMapping)
        {
            // A corrected code changes the UPC worked out from it, unless someone typed their own.
            bool upcWasDerived = upc.Length == 0
                || (Upc.Classify(item.RawCode) == CodeKind.Upc11 && upc == Upc.ReceiptCodeToRasUpc(item.RawCode));
            if (code != item.RawCode && upcWasDerived)
                upc = Upc.Classify(code) == CodeKind.Upc11 ? Upc.ReceiptCodeToRasUpc(code) : "";

            if (Upc.Classify(code) == CodeKind.Unknown)
            {
                DetailError = "The UPC on the receipt should be digits only. Check it against the receipt image.";
                return;
            }
            if (NameText.Trim().Length == 0) { DetailError = "Enter the item's name."; return; }
            if (SkuText.Trim().Length == 0 && upc.Length == 0)
            {
                DetailError = "Enter the item's Estream SKU or UPC, so Estream can find it.";
                return;
            }
            if (!SkuText.Trim().All(char.IsAsciiDigit) || !upc.All(char.IsAsciiDigit))
            {
                DetailError = "SKU and UPC should be digits only.";
                return;
            }
        }

        foreach (var e in edits) Session.Apply(e);
        if (needsMapping)
        {
            Session.SaveMapping(new ItemMapping(code, SkuText.Trim(), upc, NameText.Trim(), ListingText.Trim(), ContainerText.Trim()));
        }
        Session.Apply(new ReceiptEdit(EditKind.ConfirmLine, line));

        var after = Session.Review.Lines.FirstOrDefault(x => x.Item.SourceLine == line);
        Refresh(after?.State == LineState.Attention ? line : null, selectFirstAttention: after?.State != LineState.Attention);
    }

    private void SaveTotals()
    {
        var r = Session.Receipt;
        var edits = new List<ReceiptEdit>();
        if (Changed(SubtotalText, r.Subtotal, out var s, "subtotal")) edits.Add(new(EditKind.SetSubtotal, Value: Str(s)));
        if (Changed(TaxText, r.Tax, out var t, "tax")) edits.Add(new(EditKind.SetTax, Value: Str(t)));
        if (Changed(GrandTotalText, r.Total, out var g, "total")) edits.Add(new(EditKind.SetTotal, Value: Str(g)));
        if (Changed(CountText, r.ItemCount, out var c, "item count")) edits.Add(new(EditKind.SetItemCount, Value: Str(c)));
        if (HasDetailError) return;
        foreach (var e in edits) Session.Apply(e);
        Refresh(TotalsLine);
    }

    private void AddUnreadableAsItem(RowVm row)
    {
        if (NameText.Trim().Length == 0 || CodeText.Trim().Length == 0)
        {
            DetailError = "Enter a UPC and name, or choose Ignore this line.";
            return;
        }
        if (!TryDec(QuantityText, out var qty) || !TryDec(UnitText, out var price))
        {
            DetailError = "Check the new unit cost and quantity. They should be numbers.";
            return;
        }
        ApplyToSelected(new ReceiptEdit(EditKind.AddItem, row.Line, CodeText.Trim(), NameText.Trim(), qty, price));
    }

    private void DoNotForResale()
    {
        var l = _selected!.Review!;
        var m = l.Mapping ?? Core.Review.NewMappingDraft(l.Item);
        Session.SaveMapping(m with { ReceiptCode = l.Item.RawCode, NotForResale = true });
        Refresh(null, selectFirstAttention: true);
    }

    private void DoForce()
    {
        DetailError = null;
        if (ForceText.Trim().Length < 5)
        {
            DetailError = "Say why the totals can't be matched, so there's a record of it.";
            return;
        }
        Session.Apply(new ReceiptEdit(EditKind.ForceExport, Value: ForceText.Trim()));
        Refresh(null, selectFirstAttention: true);
    }

    private void DoExport()
    {
        PageError = null;
        // Creating a file before everything's checked takes a second click, once the footer has said what that means.
        bool early = !Session.Review.CanExport;
        if (early && !_exportArmed)
        {
            _exportArmed = true;
            Raise(nameof(FooterText));
            Raise(nameof(ExportLabel));
            return;
        }
        try
        {
            var outcome = Session.Export(includeUnchecked: early);
            _main.ShowDone(Session.Record, outcome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Log.Error("Export", ex);
            PageError = "The price file couldn't be saved. Check the save folder in Settings, then try again.";
        }
    }

    private void DoDiscard()
    {
        if (!_discardArmed)
        {
            _discardArmed = true;
            Raise(nameof(DiscardLabel));
            return;
        }
        _main.Workflow.Store.SetStatus(Session.Record.Id, ReceiptStatus.Voided);
        _main.ShowHome();
    }

    private bool Changed(string text, decimal? current, out decimal value, string label)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (current is null) return false;
            DetailError = $"Enter the {label}.";
            return false;
        }
        if (!TryDec(text, out value))
        {
            DetailError = $"The {label} should be a number.";
            return false;
        }
        return value != current;
    }

    private static bool TryDec(string text, out decimal value) =>
        decimal.TryParse(text.Trim().TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out value) && value >= 0;

    private static string Fmt(decimal? d) => d?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
    private static string Str(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}
