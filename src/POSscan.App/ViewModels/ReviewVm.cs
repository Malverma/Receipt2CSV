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
    RowKind Kind, int Line, string Title, string Subtitle, string Amount, string Status, IBrush StatusFg, IBrush StatusBg, ReviewLine? Review);

public sealed class ReviewVm : Vm
{
    private const int TotalsLine = -100;
    private const double BaseImageWidth = 396;

    private static readonly IBrush WarnFg = Brush.Parse("#8A4B00"), WarnBg = Brush.Parse("#FFF4DC");
    private static readonly IBrush GoodFg = Brush.Parse("#0B6B3A"), GoodBg = Brush.Parse("#E3F6EA");
    private static readonly IBrush PlainFg = Brush.Parse("#5D6778"), PlainBg = Brush.Parse("#EEF0F3");
    private static readonly IBrush InfoFg = Brush.Parse("#0F6CBD"), InfoBg = Brush.Parse("#EBF3FC");

    private readonly MainVm _main;
    public ReceiptSession Session { get; }

    private RowVm? _selected;
    private bool _isRebuilding, _discardArmed;
    private double _zoom = 1;
    private string? _detailError, _pageError;
    private string _code = "", _qty = "", _unit = "", _total = "", _group = "", _name = "", _sku = "", _upc = "", _listing = "", _container = "";
    private string _subtotal = "", _tax = "", _grandTotal = "", _count = "", _force = "";

    public ObservableCollection<RowVm> Rows { get; } = [];
    public ObservableCollection<string> Reasons { get; } = [];
    public ObservableCollection<FixVm> Fixes { get; } = [];
    public Bitmap? Preview { get; }

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
            if (r.AttentionCount > 0) return r.AttentionCount == 1 ? "1 thing to check before you can create the file" : $"{r.AttentionCount} things to check before you can create the file";
            int n = r.ToExport.Count();
            return n == 0 ? "No costs have changed, so there's nothing to send to Estream." : n == 1 ? "1 item is ready to update in Estream" : $"{n} items are ready to update in Estream";
        }
    }

    public bool CanExport => Session.Review.CanExport;
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
    public string PriceLabel => _selected?.Review?.Item.Unit == PriceUnit.Kg ? "Price per kg" : IsUnreadable ? "Price each" : "Price";
    public string DetailTitle => _selected?.Title ?? "";
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
    public double ImageWidth => BaseImageWidth * _zoom;
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
        Export = new Command(DoExport, () => CanExport);
        Discard = new Command(DoDiscard);
        ZoomIn = new Command(() => SetZoom(Math.Min(_zoom + 0.5, 3)));
        ZoomOut = new Command(() => SetZoom(Math.Max(_zoom - 0.5, 1)));
        Refresh(null, selectFirstAttention: true);
    }

    private void SetZoom(double zoom)
    {
        _zoom = zoom;
        Raise(nameof(ImageWidth));
        Raise(nameof(ImageHeight));
        RaiseHighlight();
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
        // Include the price line under a two-line item.
        int last = _selected.Review?.Item is { HasPrefixQuantity: true } or { Unit: PriceUnit.Kg } && first + 1 < lines.Count ? first + 1 : first;
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
        if (!review.TotalsMatch && !review.Forced) Rows.Add(totals);
        foreach (var r in attention) Rows.Add(r);
        foreach (var r in rows.Where(r => r.Review!.State == LineState.Attention)) Rows.Add(r);
        foreach (var r in rows.Where(r => r.Review!.State == LineState.Ready)) Rows.Add(r);
        foreach (var r in rows.Where(r => r.Review!.State == LineState.ByHand)) Rows.Add(r);
        foreach (var r in rows.Where(r => r.Review!.State == LineState.NoChange)) Rows.Add(r);
        foreach (var r in rows.Where(r => r.Review!.State is LineState.Skipped or LineState.NotForResale)) Rows.Add(r);
        if (review.TotalsMatch || review.Forced) Rows.Add(totals);
        _isRebuilding = false;

        RowVm? next = keepLine is { } k ? Rows.FirstOrDefault(r => r.Line == k) : null;
        if (selectFirstAttention || next is null) next = Rows.FirstOrDefault(NeedsAttention) ?? next ?? Rows.FirstOrDefault();
        _selected = null;
        Selected = next;
        Raise(nameof(Selected));

        foreach (var p in new[] { nameof(TotalsChip), nameof(TotalsFg), nameof(TotalsBg), nameof(FooterText), nameof(CanExport), nameof(HeaderDate) }) Raise(p);
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
            LineState.Attention => (l.IsNew && l.Reasons.Count == 1 && l.Reasons[0].StartsWith("New", StringComparison.Ordinal) ? "New" : "Check", WarnFg, WarnBg),
            LineState.Ready => ("Ready", GoodFg, GoodBg),
            LineState.ByHand => ("By hand", InfoFg, InfoBg),
            LineState.NoChange => ("No change", PlainFg, PlainBg),
            LineState.Skipped => ("Skipped", PlainFg, PlainBg),
            _ => ("Not for resale", PlainFg, PlainBg),
        };
        var qty = l.Item.Unit == PriceUnit.Kg ? $"{l.Item.Quantity:0.###} kg" : $"Qty {l.Item.Quantity:0.###}";
        var amount = l.Cost is { } c ? (l.Item.Unit == PriceUnit.Kg ? $"{c:0.00}/kg" : $"{c:0.00}") : "?";
        var title = l.DisplayName.Length > 0 ? l.DisplayName : "(no name)";
        return new(RowKind.Item, l.Item.SourceLine, title, $"{l.Item.RawCode}  ·  {qty}", amount, status, fg, bg, l);
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
            nameof(HasSelection), nameof(IsItem), nameof(IsTotals), nameof(IsUnreadable), nameof(ShowsCodeAndPrice), nameof(IsGroup), nameof(IsSingleAmount),
            nameof(ShowsLineTotal), nameof(ShowsMapping), nameof(IsSkipped), nameof(IsNotForResale), nameof(ShowsActions), nameof(HasFixes), nameof(HasReasons),
            nameof(CanForce), nameof(QuantityLabel), nameof(PriceLabel), nameof(DetailTitle), nameof(CostSummary), nameof(ConfirmLabel),
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
            if (Changed(UnitText, item.UnitPrice, out var unit, "price")) edits.Add(new(EditKind.SetUnitPrice, line, Str(unit)));
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
                DetailError = "The item code should be digits only. Check it against the receipt image.";
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
            DetailError = "Enter a code and name, or choose Ignore this line.";
            return;
        }
        if (!TryDec(QuantityText, out var qty) || !TryDec(UnitText, out var price))
        {
            DetailError = "Check the quantity and price. They should be numbers.";
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
        try
        {
            var outcome = Session.Export();
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
