using POSscan.Core;
using POSscan.Data;

namespace POSscan.Tests;

public class WorkflowTests : IDisposable
{
    private sealed class FixtureReader(IReadOnlyList<OcrLine> lines, string? barcode = "99152802690720261027134915") : IReceiptReader
    {
        public int Calls { get; private set; }
        public IReadOnlyList<ReaderResult> Read(string pdfPath)
        {
            Calls++;
            return [new(lines, barcode, [1, 2, 3], 1275, 5922)];
        }
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("posscan-flow").FullName;
    private readonly PosStore _store;
    private readonly List<OcrLine> _lines = File.ReadAllLines(Path.Combine("fixtures", "superstore-ocr.txt")).Select(l => new OcrLine(l)).ToList();

    public WorkflowTests()
    {
        _store = new PosStore(Path.Combine(_dir, "data"));
        _store.SaveSettings(new AppSettings { OutputFolder = Path.Combine(_dir, "out") });
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string Pdf(string name = "a.pdf", string content = "pdf one")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private (Workflow Flow, ReceiptSession Session) Start(string? barcode = "99152802690720261027134915")
    {
        var flow = new Workflow(_store, new FixtureReader(_lines, barcode));
        return (flow, flow.Open(flow.Import(Pdf()).ReceiptId!.Value));
    }

    /// <summary>What a person does on the example: fix the misreads, then confirm each new item.</summary>
    private static void ResolveEverything(ReceiptSession s)
    {
        var fixes = new Dictionary<string, string>
        {
            ["06870012.00"] = "06870012500", ["€2930701501"] = "62930701501", ["785391/0635"] = "78539170635", ["06036314170"] = "06038314170",
        };
        foreach (var (bad, good) in fixes)
            s.Apply(new ReceiptEdit(EditKind.SetCode, s.Receipt.Items.Single(i => i.RawCode == bad).SourceLine, good));
        s.Apply(new ReceiptEdit(EditKind.SetLineTotal, s.Receipt.Items.Single(i => i.RawCode == "62796300068").SourceLine, "8.00"));
        foreach (var line in s.Review.Lines.Where(l => l.State == LineState.Attention && l.Mapping is null).ToList())
            s.SaveMapping(Review.NewMappingDraft(line.Item) with { Sku = line.Item.CodeKind == CodeKind.InStore7 ? line.Item.RawCode : "" });
    }

    [Fact]
    public void First_receipt_every_new_item_needs_a_person()
    {
        var (_, s) = Start();
        Assert.False(s.Review.CanExport);
        Assert.DoesNotContain(s.Review.Lines, l => l.State == LineState.Ready);
    }

    [Fact]
    public void Example_receipt_runs_to_a_valid_file_with_correct_costs()
    {
        var (_, s) = Start();
        ResolveEverything(s);
        Assert.True(s.Review.TotalsMatch);
        Assert.True(s.Review.CanExport);

        var outcome = s.Export();

        var file = RasCsv.Read(outcome.Path);
        Assert.Empty(file.Errors);
        Assert.Equal(outcome.Rows, file.Items.Count);
        Assert.Equal(22, file.Items.Count); // 24 lines less the 2 weighed cabbages.
        Assert.Equal(5.05m, file.Items.Single(i => i.Upc == "68700112003").NewPrice);
        Assert.Equal(3.50m, file.Items.Single(i => i.Sku == "2439450").NewPrice); // Before its BOGO discount.
        Assert.Equal(3.00m, file.Items.Single(i => i.Upc == Upc.ReceiptCodeToRasUpc("62883487114")).NewPrice); // Multi-buy.
        Assert.Equal(2, outcome.ByHand.Count);
        Assert.Contains("per kg", outcome.ByHand[0]);
        Assert.Equal(ReceiptStatus.Exported, _store.GetReceipt(s.Record.Id).Status);
    }

    [Fact]
    public void Same_file_is_caught_before_reading_it_again()
    {
        var reader = new FixtureReader(_lines);
        var flow = new Workflow(_store, reader);
        var first = flow.Import(Pdf());
        var second = flow.Import(Pdf());
        Assert.Null(second.ReceiptId);
        Assert.Equal(first.ReceiptId, second.DuplicateOf);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public void Second_scan_of_the_same_receipt_is_caught_by_transaction_id()
    {
        var flow = new Workflow(_store, new FixtureReader(_lines));
        var first = flow.Import(Pdf("a.pdf", "scan one"));
        var second = flow.Import(Pdf("b.pdf", "scan two, different bytes"));
        Assert.Equal(first.ReceiptId, second.DuplicateOf);
    }

    [Fact]
    public void A_person_can_override_a_duplicate_and_it_is_recorded()
    {
        var flow = new Workflow(_store, new FixtureReader(_lines));
        flow.Import(Pdf());
        var again = flow.Import(Pdf(), allowDuplicate: true);
        Assert.NotNull(again.ReceiptId);
        Assert.Contains("duplicate-allowed", _store.AuditActions(again.ReceiptId!.Value));
    }

    [Fact]
    public void Edits_survive_a_restart()
    {
        var (flow, s) = Start();
        var basil = s.Receipt.Items.Single(i => i.RawCode == "62796300068").SourceLine;
        s.Apply(new ReceiptEdit(EditKind.SetLineTotal, basil, "8.00"));

        var reopened = flow.Open(s.Record.Id);

        Assert.Equal(179.79m, reopened.Check.CalculatedSubtotal);
    }

    [Fact]
    public void Original_pdf_is_archived_read_only()
    {
        var (_, s) = Start();
        Assert.True(File.Exists(s.Record.ArchivePath));
        Assert.True(File.GetAttributes(s.Record.ArchivePath).HasFlag(FileAttributes.ReadOnly));
    }

    private ReceiptSession SecondReceipt(Workflow flow, decimal milkPrice)
    {
        // A later receipt: the same milk at a new price, with its own totals.
        var total = milkPrice;
        var lines = new List<OcrLine>
        {
            new("REAL CANADIAN"), new("22 -DAIRY"), new($"06870011200 HOMO MILK RQ {milkPrice:0.00}"),
            new($"SUBTOTAL {total:0.00}"), new("G=GST 5% 0.00 @ 5.000% 0.00"), new($"TOTAL {total:0.00}"), new("Number of Items: 1"),
            new("2026/11/20 02 1234 10:00"),
        };
        var later = new Workflow(_store, new FixtureReader(lines, "99152802123420261120100000"));
        return later.Open(later.Import(Pdf($"later{milkPrice}.pdf", $"later {milkPrice}")).ReceiptId!.Value);
    }

    [Fact]
    public void Next_receipt_uses_saved_items_and_shows_the_old_cost()
    {
        var (flow, s) = Start();
        ResolveEverything(s);
        s.Export();

        var next = SecondReceipt(flow, 5.25m);
        var milk = next.Review.Lines.Single();

        Assert.Equal(LineState.Ready, milk.State);
        Assert.Equal(5.05m, milk.OldCost);
        var row = RasCsv.Parse(RasCsv.Write(Review.ToBatch(next.Review, "x"), new CsvProfile(), DateTime.Now, DateOnly.MinValue)).Items.Single();
        Assert.Equal(0.20m, row.Diff);
    }

    [Fact]
    public void Same_cost_as_last_time_has_nothing_to_send()
    {
        var (flow, s) = Start();
        ResolveEverything(s);
        s.Export();

        var next = SecondReceipt(flow, 5.05m);

        Assert.Equal(LineState.NoChange, next.Review.Lines.Single().State);
        Assert.False(next.Review.CanExport);
    }

    [Fact]
    public void Big_price_jump_waits_for_a_person()
    {
        var (flow, s) = Start();
        ResolveEverything(s);
        s.Export();

        var next = SecondReceipt(flow, 9.00m);
        var line = next.Review.Lines.Single();
        Assert.Equal(LineState.Attention, line.State);
        Assert.Contains(line.Reasons, r => r.Contains("Cost changed"));

        next.Apply(new ReceiptEdit(EditKind.ConfirmLine, line.Item.SourceLine));
        Assert.Equal(LineState.Ready, next.Review.Lines.Single().State);

        // Changing the price afterwards cancels the confirmation.
        next.Apply(new ReceiptEdit(EditKind.SetUnitPrice, line.Item.SourceLine, "9.50"));
        Assert.Equal(LineState.Attention, next.Review.Lines.Single().State);
    }

    [Fact]
    public void Misread_code_near_a_saved_item_offers_a_fix_and_is_not_matched()
    {
        var (flow, s) = Start();
        s.SaveMapping(new ItemMapping("06038314170", "", "60383141701", "PC MUSHROOM SLICED"));
        var line = s.Review.Lines.Single(l => l.Item.RawCode == "06036314170");

        Assert.Equal(LineState.Attention, line.State);
        Assert.Null(line.Mapping);
        var fix = Assert.Single(line.Fixes);
        Assert.Contains("PC MUSHROOM SLICED", fix.Label);

        s.Apply(fix.Edit);
        Assert.Equal("PC MUSHROOM SLICED", s.Review.Lines.Single(l => l.Item.SourceLine == line.Item.SourceLine).DisplayName);
    }

    [Fact]
    public void Older_receipt_cannot_silently_replace_a_newer_cost()
    {
        var (flow, s) = Start();
        ResolveEverything(s);
        var newer = SecondReceipt(flow, 5.25m);
        newer.Export();

        s.Rebuild();
        var milk = s.Review.Lines.Single(l => l.Item.RawCode == "06870011200");

        Assert.Equal(LineState.Attention, milk.State);
        Assert.Contains(milk.Reasons, r => r.Contains("newer receipt"));
    }

    [Fact]
    public void Unmatched_totals_block_until_overridden_and_then_every_line_must_be_confirmed()
    {
        var (_, s) = Start();
        ResolveEverything(s);
        s.Apply(new ReceiptEdit(EditKind.SetSubtotal, Value: "200.00"));
        Assert.False(s.Review.TotalsMatch);
        Assert.False(s.Review.CanExport);
        Assert.Throws<InvalidOperationException>(() => Review.ToBatch(s.Review, "x"));

        s.Apply(new ReceiptEdit(EditKind.ForceExport, Value: "Receipt torn"));
        Assert.Contains(s.Review.Lines, l => l.State == LineState.Attention);
        Assert.False(s.Review.CanExport);

        foreach (var line in s.Review.Lines.Where(l => l.State == LineState.Attention).ToList())
            s.Apply(new ReceiptEdit(EditKind.ConfirmLine, line.Item.SourceLine));
        Assert.True(s.Review.CanExport);
        Assert.Contains("edit", _store.AuditActions(s.Record.Id));
    }

    [Fact]
    public void Same_item_twice_at_different_costs_uses_the_highest()
    {
        var (_, s) = Start();
        ResolveEverything(s);
        // Point two lines at one Estream item, at different costs.
        var milk = s.Review.Lines.Single(l => l.Item.RawCode == "06870011200");
        var other = s.Review.Lines.Single(l => l.Item.RawCode == "06870011500");
        s.SaveMapping(milk.Mapping! with { Sku = "500" });
        s.SaveMapping(other.Mapping! with { Sku = "500" });

        var highest = Math.Max(milk.Cost!.Value, other.Cost!.Value);
        Assert.NotEqual(milk.Cost, other.Cost);
        Assert.All(s.Review.Lines.Where(l => l.Mapping?.Sku == "500"), l => Assert.Equal(highest, l.Cost));
        Assert.Contains(s.Review.ReceiptNotes, n => n.Contains("Using the highest"));
        Assert.True(s.Review.CanExport);
        Assert.Equal(highest, RasCsv.Read(s.Export().Path).Items.Single(i => i.Sku == "500").NewPrice);
    }

    [Fact]
    public void Not_for_resale_is_remembered_and_left_out()
    {
        var (flow, s) = Start();
        ResolveEverything(s);
        var milk = s.Review.Lines.Single(l => l.Item.RawCode == "06870011200");
        s.SaveMapping(milk.Mapping! with { NotForResale = true });

        Assert.Equal(LineState.NotForResale, flow.Open(s.Record.Id).Review.Lines.Single(l => l.Item.RawCode == "06870011200").State);
        Assert.DoesNotContain(RasCsv.Read(s.Export().Path).Items, i => i.Upc == "68700112003");
    }

    [Fact]
    public void Weighed_items_export_only_when_turned_on()
    {
        var (_, s) = Start();
        Assert.Equal(LineState.ByHand, s.Review.Lines.Single(l => l.Item.RawCode == "4556").State);

        _store.SaveSettings(_store.GetSettings() with { ExportWeighed = true });
        s.Rebuild();

        Assert.Equal(LineState.Attention, s.Review.Lines.Single(l => l.Item.RawCode == "4556").State);
    }

    [Fact]
    public void Rows_only_setting_writes_just_the_items()
    {
        _store.SaveSettings(_store.GetSettings() with { FullRasLayout = false });
        var (_, s) = Start();
        ResolveEverything(s);
        var text = File.ReadAllText(s.Export().Path);
        Assert.DoesNotContain("RptID", text);
        Assert.DoesNotContain("Listing", text);
    }

    [Fact]
    public void A_second_export_never_overwrites_the_first()
    {
        var (_, s) = Start();
        ResolveEverything(s);
        var first = s.Export();
        var second = s.Export();
        Assert.NotEqual(first.Path, second.Path);
        Assert.True(File.Exists(first.Path));
    }

    [Fact]
    public void A_pdf_from_another_store_is_refused_with_a_clear_message()
    {
        var flow = new Workflow(_store, new FixtureReader([new OcrLine("SAVE-ON-FOODS"), new OcrLine("Thank you")], null));
        var ex = Assert.Throws<ReceiptReadException>(() => flow.Import(Pdf()));
        Assert.Contains("Superstore", ex.Message);
    }

    private sealed class StackReader(params IReadOnlyList<OcrLine>[] receipts) : IReceiptReader
    {
        public IReadOnlyList<ReaderResult> Read(string pdfPath) =>
            receipts.Select(lines => new ReaderResult(lines, null, [1, 2, 3], 1275, 5922)).ToList();
    }

    private static List<OcrLine> Stack(int n) =>
        File.ReadAllLines(Path.Combine("fixtures", $"stack-receipt-{n}-ocr.txt")).Select(l => new OcrLine(l)).ToList();

    [Fact]
    public void A_stack_of_receipts_becomes_one_receipt_each()
    {
        var flow = new Workflow(_store, new StackReader(Stack(1), Stack(2), Stack(3)));
        var outcome = flow.Import(Pdf());
        Assert.Equal(3, outcome.Added.Count);
        Assert.Equal(outcome.Added[0], outcome.ReceiptId);
        Assert.Equal(380.46m, flow.Open(outcome.Added[0]).Receipt.Subtotal);
        Assert.Equal(220.06m, flow.Open(outcome.Added[1]).Receipt.Subtotal);
        Assert.Null(flow.Open(outcome.Added[2]).Receipt.Subtotal);
    }

    [Fact]
    public void Adding_the_same_stack_again_is_caught()
    {
        var flow = new Workflow(_store, new StackReader(Stack(1), Stack(2)));
        var first = flow.Import(Pdf());
        var second = flow.Import(Pdf());
        Assert.Null(second.ReceiptId);
        Assert.Equal(first.Added[0], second.DuplicateOf);
    }

    [Fact]
    public void A_cut_off_receipt_in_a_stack_cannot_be_exported_but_the_others_can()
    {
        var flow = new Workflow(_store, new StackReader(Stack(2), Stack(3)));
        var outcome = flow.Import(Pdf());

        var good = flow.Open(outcome.Added[0]);
        foreach (var line in good.Review.Lines.Where(l => l.State == LineState.Attention && l.Mapping is null).ToList())
            good.SaveMapping(Review.NewMappingDraft(line.Item));
        Assert.True(good.Review.CanExport);
        var rows = RasCsv.Read(good.Export().Path).Items;
        Assert.Contains(rows, r => r.Upc == Upc.ReceiptCodeToRasUpc("06870001100") && r.NewPrice == 6.11m);
        // Bought across a deal and past its limit: the higher price applies to all of them.
        Assert.Contains(rows, r => r.Upc == Upc.ReceiptCodeToRasUpc("06620000498") && r.NewPrice == 1.49m);

        var cut = flow.Open(outcome.Added[1]);
        foreach (var line in cut.Review.Lines.Where(l => l.State == LineState.Attention && l.Mapping is null).ToList())
            cut.SaveMapping(Review.NewMappingDraft(line.Item));
        Assert.False(cut.Review.CanExport);
        Assert.Contains(cut.Review.ReceiptProblems, p => p.Contains("bottom of this receipt"));
    }

    [Fact]
    public void Confirming_every_line_by_hand_sets_up_new_items_and_survives_a_restart()
    {
        var flow = new Workflow(_store, new StackReader(Stack(2)));
        var s = flow.Open(flow.Import(Pdf()).ReceiptId!.Value);
        var codes = new Dictionary<string, string> { ["06520090399"] = "06620090399", ["05620000502"] = "06620000502" };
        foreach (var step in ManualConfirmation.Steps(s.Receipt, s.Decisions))
        {
            var text = ManualConfirmation.Format(s.Receipt, step);
            foreach (var (bad, good) in codes) text = text.Replace(bad, good);
            Assert.Null(s.ConfirmStep(step, text));
        }
        Assert.True(s.Review.CanExport);
        Assert.Equal(43, s.Review.ToExport.Count());

        var reopened = flow.Open(s.Record.Id);
        Assert.Contains(reopened.Receipt.Items, i => i.RawCode == "06620090399");
        Assert.True(reopened.Review.CanExport);
    }

    [Fact]
    public void A_code_one_digit_from_a_known_item_is_pointed_out_and_can_still_be_confirmed()
    {
        var lines = new[] { "21-GROCERY", "02970080141 IDAH BRY MRJ 1.99", "02970080147 RSTD GRLIC MRJ 1.99", "SUBTOTAL 3.98", "TOTAL 3.98" }.Select(l => new OcrLine(l)).ToList();
        var flow = new Workflow(_store, new StackReader(lines));
        var s = flow.Open(flow.Import(Pdf()).ReceiptId!.Value);
        var steps = ManualConfirmation.Steps(s.Receipt, s.Decisions);
        Assert.Null(s.ConfirmStep(steps[0], "02970080141 1.99 x 1"));
        Assert.NotEmpty(s.NearMisses(steps[1].Line));
        Assert.Null(s.ConfirmStep(steps[1], "02970080147 1.99 x 1"));
        Assert.Empty(s.NearMisses(steps[1].Line));
        Assert.Equal(LineState.Ready, s.Review.Lines.Single(l => l.Item.RawCode == "02970080147").State);
    }

    private ReceiptSession OpenLines(params string[] text)
    {
        var flow = new Workflow(_store, new StackReader(text.Select(l => new OcrLine(l)).ToList()));
        return flow.Open(flow.Import(Pdf(content: string.Join('\n', text))).ReceiptId!.Value);
    }

    [Fact]
    public void Looking_at_a_line_does_not_mark_it_checked_but_confirming_or_changing_it_does()
    {
        var s = OpenLines("21-GROCERY", "02970080141 IDAH BRY MRJ 1.99", "06038314170 PCO MUSH MRJ 3.50", "SUBTOTAL 5.49", "TOTAL 5.49");
        ReviewLine Line(string code) => s.Review.Lines.Single(l => l.Item.RawCode == code);
        Assert.All(s.Review.Lines, l => Assert.False(l.IsChecked));

        s.Apply(new ReceiptEdit(EditKind.ConfirmLine, Line("02970080141").Item.SourceLine));
        Assert.True(Line("02970080141").IsChecked);
        Assert.False(Line("06038314170").IsChecked);

        s.Apply(new ReceiptEdit(EditKind.SetLineTotal, Line("06038314170").Item.SourceLine, "3.50"));
        Assert.True(Line("06038314170").IsChecked);
    }

    [Fact]
    public void Renaming_a_saved_item_in_the_walk_updates_its_saved_name()
    {
        var s = OpenLines("21-GROCERY", "02970080141 IDAH BRY MRJ 1.99", "SUBTOTAL 1.99", "TOTAL 1.99");
        var step = ManualConfirmation.Steps(s.Receipt, s.Decisions)[0];
        Assert.Null(s.ConfirmStep(step, s.FormatStep(step)));
        Assert.Equal("IDAH BRY", s.Review.Lines.Single().DisplayName);
        Assert.Null(s.ConfirmStep(step, "02970080141  IDAHO BERRY  1.99 x 1"));
        Assert.Equal("IDAHO BERRY", s.Review.Lines.Single().Mapping!.EstreamName);
        Assert.Equal("02970080141  IDAHO BERRY  1.99 x 1", s.FormatStep(step));
    }

    [Fact]
    public void A_file_can_be_created_before_anything_is_checked_and_says_what_was_left_out()
    {
        var s = OpenLines("21-GROCERY", "02970080141 IDAH BRY MRJ 1.99", "2439450 BAKERY BUN MRJ 3.00", "SUBTOTAL 9.99", "TOTAL 9.99");
        Assert.False(s.Review.CanExport);
        Assert.True(s.Review.CanExportUnchecked);
        Assert.Throws<InvalidOperationException>(() => s.Export());

        var outcome = s.Export(includeUnchecked: true);

        var row = Assert.Single(RasCsv.Read(outcome.Path).Items);
        Assert.Equal(Upc.ReceiptCodeToRasUpc("02970080141"), row.Upc);
        Assert.Equal(1.99m, row.NewPrice);
        Assert.Contains(outcome.ByHand, b => b.Contains("BAKERY BUN") && b.Contains("SKU or UPC"));
        Assert.Contains("1 item in this file wasn't checked", outcome.Warning);
        Assert.Contains("totals weren't matched", outcome.Warning);
        Assert.Contains("exported-unchecked", _store.AuditActions(s.Record.Id));
        Assert.Empty(_store.GetMappings(s.Record.Supplier)); // Nothing unchecked is saved as a known item.
    }

    [Fact]
    public void An_early_file_leaves_out_codes_that_look_misread_and_uses_the_highest_cost_for_repeats()
    {
        var first = OpenLines("21-GROCERY", "02970080141 IDAH BRY MRJ 1.99", "SUBTOTAL 1.99", "TOTAL 1.99");
        first.SaveMapping(Review.NewMappingDraft(first.Review.Lines.Single().Item));

        var s = OpenLines("21-GROCERY", "02970080147 NEAR MISS MRJ 1.99", "06038314170 PCO MUSH MRJ 3.50", "06038314170 PCO MUSH MRJ 3.99", "SUBTOTAL 9.48", "TOTAL 9.48");
        var rows = RasCsv.Read(s.Export(includeUnchecked: true).Path).Items;

        var row = Assert.Single(rows);
        Assert.Equal(3.99m, row.NewPrice);
    }

    [Fact]
    public void A_file_finished_normally_has_no_warning()
    {
        var s = OpenLines("21-GROCERY", "02970080141 IDAH BRY MRJ 1.99", "SUBTOTAL 1.99", "TOTAL 1.99");
        var step = ManualConfirmation.Steps(s.Receipt, s.Decisions)[0];
        Assert.Null(s.ConfirmStep(step, s.FormatStep(step)));
        Assert.True(s.Review.CanExport);
        Assert.Null(s.Export().Warning);
    }

    [Fact]
    public void Text_that_does_not_read_is_refused_and_nothing_is_saved()
    {
        var flow = new Workflow(_store, new StackReader(Stack(2)));
        var s = flow.Open(flow.Import(Pdf()).ReceiptId!.Value);
        var step = ManualConfirmation.Steps(s.Receipt, s.Decisions)[0];
        Assert.NotNull(s.ConfirmStep(step, "not numbers"));
        Assert.Empty(flow.Open(s.Record.Id).Record.Edits);
    }

    [Fact]
    public void An_in_store_code_stays_flagged_because_it_needs_a_sku()
    {
        var (_, s) = Start();
        var step = ManualConfirmation.Steps(s.Receipt, s.Decisions).First(x => x.Kind == ConfirmKind.Item && s.Receipt.Items.Single(i => i.SourceLine == x.Line).RawCode == "2439450");
        Assert.Null(s.ConfirmStep(step, ManualConfirmation.Format(s.Receipt, step)));
        Assert.Equal(LineState.Attention, s.Review.Lines.Single(l => l.Item.RawCode == "2439450").State);
    }

    [Fact]
    public void Finishing_the_walk_is_remembered()
    {
        var (flow, s) = Start();
        Assert.False(s.ManualConfirmationDone);
        s.MarkManualConfirmationDone();
        s.MarkManualConfirmationDone();
        Assert.True(flow.Open(s.Record.Id).ManualConfirmationDone);
        Assert.Single(_store.AuditActions(s.Record.Id), a => a == "manual-confirmation-done");
    }

    [Fact]
    public void Missing_file_gives_a_clear_message()
    {
        var flow = new Workflow(_store, new FixtureReader(_lines));
        Assert.Throws<ReceiptReadException>(() => flow.Import(Path.Combine(_dir, "nope.pdf")));
    }

    [Fact]
    public void Daily_backup_is_created_once_and_old_ones_are_removed()
    {
        for (int i = 0; i < 35; i++) _store.BackupIfDue(new DateTime(2026, 1, 1).AddDays(i));
        var backups = Directory.GetFiles(Path.Combine(_store.DataDir, "backups"), "daily-*.db");
        Assert.Equal(30, backups.Length);
        Assert.DoesNotContain(backups, b => b.EndsWith("2026-01-01.db", StringComparison.Ordinal));
    }

    [Fact]
    public void Data_from_a_newer_version_is_refused_not_damaged()
    {
        var dir = Path.Combine(_dir, "future");
        using (var s = new PosStore(dir)) { }
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "posscan.db")};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new PosStore(dir));
    }
}
