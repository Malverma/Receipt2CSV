using System.Globalization;
using System.Text.Json;
using POSscan.Core;
using POSscan.Data;
using POSscan.Ocr;

return args switch
{
    ["read", var pdf, .. var rest] => Read(pdf, rest),
    ["batch", var pdf, var outDir, .. var rest] => Batch(pdf, outDir, rest),
    ["read-text", var txt] => Report(File.ReadAllLines(txt).Select(l => new OcrLine(l)).ToList(), null),
    ["check-csv", var csv] => CheckCsv(csv),
    ["export", var json, var csv] => Export(json, csv),
    ["make-probes", var dir] => Probes.Make(dir),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        POSscan command line (development build)

          read <receipt.pdf> [--dump lines.txt]   OCR a receipt and check it against its printed totals
          read-text <lines.txt>                   Same check, using saved OCR lines
          batch <file.pdf> <folder> [--fix a=b]   Test run: import, accept new items as read, and write a CSV per
                                                  receipt that passes. Uses a throwaway database.
          check-csv <file.csv>                    Read a RAS-style CSV and validate it
          export <batch.json> <out.csv>           Write a reviewed batch as a RAS-style CSV
          make-probes <folder>                    Write the Milestone 0 Estream test files
        """);
    return 2;
}

static int Read(string pdf, string[] rest)
{
    using var reader = new ReceiptReader();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var receipts = reader.Read(pdf);
    Console.WriteLine($"Read in {watch.Elapsed.TotalSeconds:0.0}s. Found {receipts.Count} receipt(s).");
    int worst = 0;
    for (int i = 0; i < receipts.Count; i++)
    {
        var read = receipts[i];
        Console.WriteLine($"\n===== Receipt {i + 1} of {receipts.Count}. Barcode: {read.Barcode ?? "not read"} =====");
        if (rest is ["--dump", var path])
            File.WriteAllLines(receipts.Count == 1 ? path : $"{path}.{i + 1}.txt", read.Lines.Select(l => l.Text));
        worst = Math.Max(worst, Report(read.Lines, read.Barcode));
    }
    return worst;
}

static int Batch(string pdf, string outDir, string[] rest)
{
    var fixes = rest.Where(r => r.Contains('=')).Select(r => r.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    var data = Directory.CreateTempSubdirectory("posscan-batch").FullName;
    int blocked = 0;
    try
    {
        using var reader = new ReceiptReader();
        using var store = new PosStore(data);
        store.SaveSettings(new AppSettings { OutputFolder = Path.GetFullPath(outDir) });
        var flow = new Workflow(store, reader);
        var outcome = flow.Import(pdf);
        Console.WriteLine($"Found {outcome.Added.Count} receipt(s).");
        foreach (var (id, n) in outcome.Added.Select((id, i) => (id, i + 1)))
        {
            var s = flow.Open(id);
            foreach (var item in s.Receipt.Items.ToList())
                if (fixes.TryGetValue(item.RawCode, out var good))
                {
                    Console.WriteLine($"  Corrected {item.RawCode} to {good}");
                    s.Apply(new ReceiptEdit(EditKind.SetCode, item.SourceLine, good));
                }
            foreach (var line in s.Review.Lines.Where(l => l.State == LineState.Attention && l.Mapping is null).ToList())
                s.SaveMapping(Review.NewMappingDraft(line.Item) with { Sku = line.Item.CodeKind == CodeKind.InStore7 ? line.Item.RawCode : "" });

            Console.WriteLine($"\nReceipt {n}: subtotal {s.Receipt.Subtotal?.ToString("0.00") ?? "not read"}, lines add up to {s.Check.CalculatedSubtotal:0.00}");
            foreach (var note in s.Review.ReceiptNotes) Console.WriteLine($"  Note: {note}");
            if (!s.Review.CanExport)
            {
                blocked++;
                Console.WriteLine($"  NOT EXPORTED: {s.Review.AttentionCount} things still need a person.");
                foreach (var p in s.Review.ReceiptProblems) Console.WriteLine($"    {p}");
                foreach (var (_, text) in s.Review.Unreadable.Take(5)) Console.WriteLine($"    Unreadable: {text}");
                foreach (var l in s.Review.Lines.Where(l => l.State == LineState.Attention).Take(5))
                    Console.WriteLine($"    {l.Item.RawCode} {l.Item.Name}: {string.Join(" ", l.Reasons)}");
                continue;
            }
            var done = s.Export();
            Console.WriteLine($"  Wrote {done.Rows} rows to {done.Path}");
            foreach (var hand in done.ByHand) Console.WriteLine($"  By hand: {hand}");
        }
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(data, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(data, recursive: true);
    }
    return blocked == 0 ? 0 : 1;
}

static int Report(IReadOnlyList<OcrLine> lines, string? barcode)
{
    var receipt = SuperstoreParser.Parse(lines);
    if (barcode is not null) receipt.TransactionId = barcode;
    var check = Reconciler.Check(receipt);

    Console.WriteLine($"Date {receipt.Date?.ToString("yyyy-MM-dd") ?? "?"}  Transaction {receipt.TransactionId ?? "?"}");
    Console.WriteLine($"{"Code",-14} {"Qty",7} {"Unit",-4} {"Printed",8} {"Cost",8} {"Fees",6}  Name");
    foreach (var i in receipt.Items)
        Console.WriteLine($"{i.RawCode,-14} {i.Quantity,7:0.###} {i.Unit,-4} {i.UnitPrice,8:0.00} {receipt.ActualUnitCost(i),8:0.00} {i.Fees,6:0.00}  {i.Name}{(i.IsNonSale ? "  [non-sale]" : "")}");
    Console.WriteLine();
    Console.WriteLine($"Subtotal: calculated {check.CalculatedSubtotal:0.00}, printed {receipt.Subtotal:0.00}");
    Console.WriteLine($"Items:    calculated {check.CalculatedItemCount}, printed {receipt.ItemCount}");
    Console.WriteLine($"Tax {receipt.Tax:0.00}  Total {receipt.Total:0.00}");
    foreach (var issue in check.Issues.OrderByDescending(i => i.Severity))
        Console.WriteLine($"  [{issue.Severity}] {issue.Code}: {issue.Message}{(issue.Suggestion is null ? "" : $" Suggested: {issue.Suggestion}")}");
    foreach (var fix in check.GapFixes)
        Console.WriteLine("  Possible fix: " + string.Join(" and ", fix.Select(f => $"line {f.SourceLine}: {f.From:0.00} -> {f.To:0.00}")));
    Console.WriteLine(check.CanExport ? "RESULT: reconciled" : "RESULT: export blocked until the issues above are resolved");
    return check.CanExport ? 0 : 1;
}

static int CheckCsv(string path)
{
    var f = RasCsv.Read(path);
    Console.WriteLine($"{f.Items.Count} item rows");
    foreach (var (s, n) in f.PrintedSectionTotals) Console.WriteLine($"  {s}: printed {n}, found {f.Items.Count(i => i.Section == s)}");
    foreach (var e in f.Errors) Console.WriteLine($"  ERROR {e}");
    Console.WriteLine(f.Errors.Count == 0 && f.Items.Count > 0 ? "OK" : "FAILED");
    return f.Errors.Count == 0 && f.Items.Count > 0 ? 0 : 1;
}

static int Export(string json, string csv)
{
    var rows = JsonSerializer.Deserialize<List<BatchRow>>(File.ReadAllText(json)) ?? [];
    RasCsv.WriteAtomic(csv, rows, new CsvProfile(), DateTime.Now, DateOnly.FromDateTime(DateTime.Now));
    Console.WriteLine($"Wrote {rows.Count} rows to {csv}");
    return 0;
}

static class Probes
{
    public static int Make(string dir)
    {
        Directory.CreateDirectory(dir);
        var when = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Local);
        var eff = new DateOnly(2026, 11, 1);
        var wrapped = new CsvProfile();
        var bare = wrapped with { Name = "rows-only", Wrapper = false };

        // Real rows from the known RAS file, with prices unchanged from that file.
        BatchRow[] liquor =
        [
            new("G", "125", "80686821311", "CANADIAN CLUB - PREMIUM", "1.140", 27.40m, 26.64m, "Spirits - Whisky"),
            new("G", "3558", "83664870387", "GIBSONS FINEST 12 YR", ".750", 19.92m, 21.92m, "Spirits - Whisky"),
        ];
        // From the example receipt. Old = New, because no earlier cost is known.
        string milkUpc = Upc.ReceiptCodeToRasUpc("06870011200");
        string basilUpc = Upc.ReceiptCodeToRasUpc("62796300068");
        BatchRow[] grocery =
        [
            new("", "", milkUpc, "HOMO MILK 3.25%", "", 5.05m, 5.05m, "DAIRY"),
            new("", "", basilUpc, "BASIL 6IN", "", 4.00m, 4.00m, "GARDEN"),
        ];
        BatchRow[] weighed = [new("", "", "4556", "CABBAGE GREEN", "", 2.16m, 2.16m, "PRODUCE")];

        RasCsv.WriteAtomic(Path.Combine(dir, "probe-a-liquor-wrapped.csv"), liquor, wrapped, when, eff);
        RasCsv.WriteAtomic(Path.Combine(dir, "probe-b-liquor-rows-only.csv"), liquor, bare, when, eff);
        RasCsv.WriteAtomic(Path.Combine(dir, "probe-c-grocery-wrapped.csv"), grocery, wrapped, when, eff);
        RasCsv.WriteAtomic(Path.Combine(dir, "probe-d-weighed-wrapped.csv"), weighed, wrapped, when, eff);
        Console.WriteLine($"Wrote 4 probe files to {dir}");
        return 0;
    }
}
