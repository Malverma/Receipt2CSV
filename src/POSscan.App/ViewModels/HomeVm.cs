using System.Collections.ObjectModel;
using System.Security.Cryptography;
using POSscan.Data;

namespace POSscan.App.ViewModels;

public sealed record RecentRow(long Id, string Title, string When, string Status, Command Open);
public sealed record ScanRow(string Path, string Name, Command Open);

public sealed class HomeVm : Vm
{
    private readonly MainVm _main;
    private readonly Dictionary<string, (long Length, DateTime Modified, bool Known)> _seen = [];
    private string? _error;
    private string? _notice;
    private string? _duplicatePath;
    private long _duplicateId;

    public ObservableCollection<RecentRow> Recent { get; } = [];
    public ObservableCollection<ScanRow> Waiting { get; } = [];
    public bool HasRecent => Recent.Count > 0;
    public bool HasWaiting => Waiting.Count > 0;

    public string? Error { get => _error; set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);
    public string? Notice { get => _notice; set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(_notice);
    public bool HasDuplicate => _duplicatePath is not null;
    public string DuplicateText => $"You've already added this receipt ({Path.GetFileName(_duplicatePath ?? "")}).";

    public Command Choose { get; }
    public Command OpenSettings { get; }
    public Command OpenDuplicate { get; }
    public Command ImportAnyway { get; }
    public Command Dismiss { get; }

    public HomeVm(MainVm main)
    {
        _main = main;
        Choose = new Command(async () =>
        {
            if (await main.Shell.PickPdfAsync() is { } path) await main.ImportAsync(path);
        });
        OpenSettings = new Command(main.ShowSettings);
        OpenDuplicate = new Command(() => main.OpenReceipt(_duplicateId));
        ImportAnyway = new Command(async () =>
        {
            if (_duplicatePath is { } p) await main.ImportAsync(p, allowDuplicate: true);
        });
        Dismiss = new Command(() => { Error = null; Notice = null; SetDuplicate(null, 0); });
        LoadRecent();
        CheckScanFolder();
    }

    public void ShowDuplicate(string path, long id) => SetDuplicate(path, id);

    public async Task DropAsync(string path)
    {
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            Error = "POSscan reads PDF scans. That file isn't a PDF.";
            return;
        }
        await _main.ImportAsync(path);
    }

    private void SetDuplicate(string? path, long id)
    {
        _duplicatePath = path;
        _duplicateId = id;
        Raise(nameof(HasDuplicate));
        Raise(nameof(DuplicateText));
    }

    private void LoadRecent()
    {
        Recent.Clear();
        foreach (var r in _main.Workflow.Store.Recent(12))
        {
            var id = r.Id;
            Recent.Add(new(id, Title(r), r.ReceiptDate?.ToString("MMM d, yyyy") ?? r.CreatedUtc.ToLocalTime().ToString("MMM d, yyyy"),
                r.Status switch
                {
                    ReceiptStatus.Draft => "In progress",
                    ReceiptStatus.Exported => "File created",
                    ReceiptStatus.Imported => "Imported",
                    _ => "",
                },
                new Command(() => _main.OpenReceipt(id))));
        }
        Raise(nameof(HasRecent));
    }

    // Receipts from one stack share a day and a supplier, and often have no printed date, so the total tells them apart.
    private string Title(ReceiptSummary r)
    {
        var total = Core.SuperstoreParser.Parse(_main.Workflow.Store.GetReceipt(r.Id).Lines).Total;
        return total is { } t ? $"{r.Supplier}, total {t:0.00}" : $"{r.Supplier}, total not read";
    }

    /// <summary>
    /// Lists PDFs in the scan folder that POSscan hasn't seen. A file only counts once two checks agree on its
    /// size, because scanners write slowly and a half-written PDF looks damaged.
    /// </summary>
    public void CheckScanFolder()
    {
        var folder = _main.Workflow.Store.GetSettings().ScanFolder;
        if (folder.Length == 0 || !Directory.Exists(folder)) return;
        try
        {
            var present = Directory.GetFiles(folder, "*.pdf");
            foreach (var path in present)
            {
                var info = new FileInfo(path);
                if (_seen.TryGetValue(path, out var s) && s.Length == info.Length && s.Modified == info.LastWriteTimeUtc)
                {
                    if (s.Known || Waiting.Any(w => w.Path == path)) continue;
                    var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
                    if (_main.Workflow.Store.FindByHash(hash) is not null) { _seen[path] = s with { Known = true }; continue; }
                    var p = path;
                    Waiting.Add(new(p, Path.GetFileName(p), new Command(async () => await _main.ImportAsync(p))));
                }
                else _seen[path] = (info.Length, info.LastWriteTimeUtc, false);
            }
            foreach (var gone in Waiting.Where(w => !present.Contains(w.Path)).ToList()) Waiting.Remove(gone);
            Raise(nameof(HasWaiting));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Scan folder", ex); // Usually the scanner still has the file open. The next check retries.
        }
    }
}
