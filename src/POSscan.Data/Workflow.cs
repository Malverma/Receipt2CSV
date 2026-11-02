using System.Security.Cryptography;
using System.Text.Json;
using POSscan.Core;

namespace POSscan.Data;

public sealed record ReaderResult(IReadOnlyList<OcrLine> Lines, string? Barcode, byte[] PreviewPng, int PageWidth, int PageHeight);

public interface IReceiptReader
{
    /// <exception cref="ReceiptReadException">The PDF can't be read. The message is written for store staff.</exception>
    /// <returns>One result per receipt found in the file.</returns>
    IReadOnlyList<ReaderResult> Read(string pdfPath);
}

public sealed class ReceiptReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>ReceiptId is the first new receipt. A PDF holding a stack of receipts adds all of them to Added.</summary>
public sealed record ImportOutcome(long? ReceiptId, long? DuplicateOf)
{
    public IReadOnlyList<long> Added { get; init; } = [];
}

public sealed record ExportOutcome(string Path, int Rows, IReadOnlyList<string> ByHand, int Unchanged, int Skipped);

/// <summary>Everything the screens do, with no UI code, so the whole flow can be tested.</summary>
public sealed class Workflow(PosStore store, IReceiptReader reader)
{
    private const string Supplier = "Real Canadian Superstore";

    public PosStore Store => store;

    public ImportOutcome Import(string pdfPath, bool allowDuplicate = false)
    {
        if (!File.Exists(pdfPath)) throw new ReceiptReadException("That file couldn't be found.");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(pdfPath); }
        catch (IOException ex) { throw new ReceiptReadException("That file is in use or couldn't be opened. Try again in a moment.", ex); }
        catch (UnauthorizedAccessException ex) { throw new ReceiptReadException("POSscan doesn't have permission to open that file.", ex); }

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!allowDuplicate && store.FindByHash(hash) is { } sameFile) return new(null, sameFile);

        var reads = reader.Read(pdfPath);
        // A stack keeps every part, so a page that reads badly is shown for review rather than lost.
        if (reads.Count == 1 && !IsSuperstore(reads[0]))
            throw new ReceiptReadException("This doesn't look like a Superstore receipt, so POSscan can't read it yet.");
        if (!reads.Any(IsSuperstore))
            throw new ReceiptReadException("This doesn't look like a Superstore receipt, so POSscan can't read it yet.");

        var dir = Path.Combine(store.DataDir, "archive", DateTime.Now.ToString("yyyy"));
        Directory.CreateDirectory(dir);
        var archive = Path.Combine(dir, hash + ".pdf");
        if (!File.Exists(archive))
        {
            File.WriteAllBytes(archive, bytes);
            File.SetAttributes(archive, File.GetAttributes(archive) | FileAttributes.ReadOnly);
        }

        var added = new List<long>();
        long? duplicate = null;
        for (int part = 0; part < reads.Count; part++)
        {
            var read = reads[part];
            var parsed = SuperstoreParser.Parse(read.Lines);
            var txn = read.Barcode ?? parsed.TransactionId;
            if (!allowDuplicate && txn is not null && store.FindByTransaction(txn) is { } sameTxn) { duplicate ??= sameTxn; continue; }

            // The first part keeps the plain file hash, which is what spots the same file being added again.
            var suffix = part == 0 ? "" : $"#{part + 1}";
            var preview = Path.Combine(dir, $"{hash}{(part == 0 ? "" : $"-{part + 1}")}.png");
            File.WriteAllBytes(preview, read.PreviewPng);
            var id = store.AddReceipt(Supplier, hash + suffix, txn, parsed.Date, archive, preview, read.PageWidth, read.PageHeight, read.Lines);
            if (allowDuplicate) store.Audit(id, "duplicate-allowed", Path.GetFileName(pdfPath));
            added.Add(id);
        }
        return added.Count == 0 ? new(null, duplicate) : new(added[0], null) { Added = added };

        static bool IsSuperstore(ReaderResult read)
        {
            var parsed = SuperstoreParser.Parse(read.Lines);
            return SuperstoreParser.Matches(read.Lines) || (parsed.Items.Count > 0 && parsed.Subtotal is not null);
        }
    }

    public ReceiptSession Open(long id) => new(store, store.GetReceipt(id));
}

/// <summary>One receipt being reviewed. Every change is saved as it happens, so a crash loses nothing.</summary>
public sealed class ReceiptSession
{
    private readonly PosStore _store;
    private readonly List<ReceiptEdit> _edits;
    public ReceiptRecord Record { get; }
    public ParsedReceipt Receipt { get; private set; } = null!;
    public ReconcileResult Check { get; private set; } = null!;
    public ReviewResult Review { get; private set; } = null!;
    public DraftDecisions Decisions { get; private set; } = null!;

    internal ReceiptSession(PosStore store, ReceiptRecord record)
    {
        _store = store;
        Record = record;
        _edits = [.. record.Edits];
        Rebuild();
    }

    public void Apply(ReceiptEdit edit)
    {
        _edits.Add(edit);
        _store.SaveEdits(Record.Id, _edits, edit);
        Rebuild();
    }

    public void SaveMapping(ItemMapping mapping)
    {
        _store.SaveMapping(Record.Supplier, mapping, Record.Id);
        Rebuild();
    }

    public void RemoveMapping(string code)
    {
        _store.DeleteMapping(Record.Supplier, code, Record.Id);
        Rebuild();
    }

    public void Rebuild()
    {
        var settings = _store.GetSettings();
        (Receipt, Decisions) = ReceiptDraft.Build(Record.Lines, _edits);
        Check = Reconciler.Check(Receipt);
        Review = Core.Review.Build(Receipt, Check, Decisions, new ItemMatcher(_store.GetMappings(Record.Supplier)),
            code => _store.LookupPrice(Record.Supplier, code, Receipt.Date ?? Record.ReceiptDate, Record.Id),
            new ReviewSettings { PriceChangePercent = settings.PriceChangePercent, ExportWeighed = settings.ExportWeighed });
    }

    public ExportOutcome Export(DateTime? when = null)
    {
        var settings = _store.GetSettings();
        var rows = Core.Review.ToBatch(Review, Record.Supplier);
        var moment = when ?? DateTime.Now;

        var folder = settings.OutputFolder.Length > 0 ? settings.OutputFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "POSscan");
        var date = (Receipt.Date ?? DateOnly.FromDateTime(moment)).ToString("yyyy-MM-dd");
        var path = UniquePath(folder, $"POSscan_Superstore_{date}");

        var profile = new CsvProfile
        {
            Name = settings.FullRasLayout ? "ras-wrapped" : "rows-only", Wrapper = settings.FullRasLayout,
            StoreName = settings.StoreName, StoreNumber = settings.StoreNumber,
        };
        RasCsv.WriteAtomic(path, rows, profile, moment, DateOnly.FromDateTime(moment));

        var byHand = Review.Lines.Where(l => l.State == LineState.ByHand)
            .Select(l => $"{l.DisplayName}: {l.Cost:0.00} per kg").ToList();
        int unchanged = Review.Lines.Count(l => l.State == LineState.NoChange);
        int skipped = Review.Lines.Count(l => l.State is LineState.Skipped or LineState.NotForResale);
        var costs = Review.Lines.Where(l => l.State is LineState.Ready or LineState.NoChange && l.Cost is not null)
            .Select(l => (l.Item.RawCode, l.Cost!.Value)).ToList();
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        _store.RecordExport(Record.Id, Record.Supplier, Receipt.Date ?? Record.ReceiptDate, path, sha, rows.Count, profile.Name,
            costs, JsonSerializer.Serialize(byHand), unchanged, skipped);
        return new(path, rows.Count, byHand, unchanged, skipped);
    }

    private static string UniquePath(string folder, string stem)
    {
        var path = Path.Combine(folder, stem + ".csv");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}_{n}.csv");
        return path;
    }
}
