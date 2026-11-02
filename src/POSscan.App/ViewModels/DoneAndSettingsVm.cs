using System.Globalization;
using System.Text.Json;
using POSscan.Data;

namespace POSscan.App.ViewModels;

public sealed class DoneVm : Vm
{
    private readonly MainVm _main;
    private readonly long _receiptId;
    private bool _imported;

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public string Summary { get; }
    public IReadOnlyList<string> ByHand { get; }
    public bool HasByHand => ByHand.Count > 0;
    public bool Imported { get => _imported; private set { if (Set(ref _imported, value)) Raise(nameof(NotImported)); } }
    public bool NotImported => !_imported;
    public bool FileExists => File.Exists(FilePath);

    public Command OpenFolder { get; }
    public Command MarkImported { get; }
    public Command Another { get; }

    public DoneVm(MainVm main, ReceiptRecord record, ExportOutcome outcome)
        : this(main, record.Id, record.Status == ReceiptStatus.Imported, outcome.Path, outcome.Rows, outcome.ByHand, outcome.Unchanged, outcome.Skipped) { }

    public static DoneVm FromBatch(MainVm main, ReceiptRecord record, BatchRecord batch) =>
        new(main, record.Id, record.Status == ReceiptStatus.Imported, batch.Path, batch.RowCount,
            JsonSerializer.Deserialize<List<string>>(batch.ByHandJson) ?? [], batch.Unchanged, batch.Skipped);

    private DoneVm(MainVm main, long receiptId, bool imported, string path, int rows, IReadOnlyList<string> byHand, int unchanged, int skipped)
    {
        _main = main;
        _receiptId = receiptId;
        _imported = imported;
        FilePath = path;
        ByHand = byHand;
        var parts = new List<string> { rows == 1 ? "1 item to update" : $"{rows} items to update" };
        if (unchanged > 0) parts.Add($"{unchanged} unchanged");
        if (skipped > 0) parts.Add($"{skipped} skipped");
        Summary = string.Join(CultureInfo.InvariantCulture.TextInfo.ListSeparator + " ", parts);

        OpenFolder = new Command(() => main.Shell.OpenFolder(Path.GetDirectoryName(FilePath)!));
        MarkImported = new Command(() =>
        {
            main.Workflow.Store.SetStatus(_receiptId, ReceiptStatus.Imported);
            Imported = true;
        });
        Another = new Command(() => main.ShowHome());
    }
}

public sealed class SettingsVm : Vm
{
    private readonly MainVm _main;
    private string _storeName, _storeNumber, _output, _scan, _percent;
    private bool _weighed, _fullLayout;
    private string? _error;

    public string StoreName { get => _storeName; set => Set(ref _storeName, value); }
    public string StoreNumber { get => _storeNumber; set => Set(ref _storeNumber, value); }
    public string OutputFolder { get => _output; set => Set(ref _output, value); }
    public string ScanFolder { get => _scan; set => Set(ref _scan, value); }
    public string Percent { get => _percent; set => Set(ref _percent, value); }
    public bool ExportWeighed { get => _weighed; set => Set(ref _weighed, value); }
    public bool FullLayout { get => _fullLayout; set => Set(ref _fullLayout, value); }
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);
    public string DataFolder => _main.Workflow.Store.DataDir;

    public Command Save { get; }
    public Command Cancel { get; }
    public Command ChooseOutput { get; }
    public Command ChooseScan { get; }
    public Command OpenDataFolder { get; }

    public SettingsVm(MainVm main)
    {
        _main = main;
        var s = main.Workflow.Store.GetSettings();
        _storeName = s.StoreName;
        _storeNumber = s.StoreNumber;
        _output = s.OutputFolder.Length > 0 ? s.OutputFolder : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "POSscan");
        _scan = s.ScanFolder;
        _percent = s.PriceChangePercent.ToString("0.##", CultureInfo.InvariantCulture);
        _weighed = s.ExportWeighed;
        _fullLayout = s.FullRasLayout;

        Save = new Command(DoSave);
        Cancel = new Command(() => main.ShowHome());
        ChooseOutput = new Command(async () => { if (await main.Shell.PickFolderAsync("Where should price files be saved?") is { } f) OutputFolder = f; });
        ChooseScan = new Command(async () => { if (await main.Shell.PickFolderAsync("Which folder does your scanner save to?") is { } f) ScanFolder = f; });
        OpenDataFolder = new Command(() => main.Shell.OpenFolder(DataFolder));
    }

    private void DoSave()
    {
        if (!decimal.TryParse(Percent.Trim().TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var pct) || pct <= 0)
        {
            Error = "The price change warning should be a number above 0, such as 25.";
            return;
        }
        if (StoreName.Trim().Length == 0)
        {
            Error = "Enter your store's name.";
            return;
        }
        _main.Workflow.Store.SaveSettings(_main.Workflow.Store.GetSettings() with
        {
            StoreName = StoreName.Trim().ToUpperInvariant(), StoreNumber = StoreNumber.Trim(), OutputFolder = OutputFolder.Trim(),
            ScanFolder = ScanFolder.Trim(), PriceChangePercent = pct, ExportWeighed = ExportWeighed, FullRasLayout = FullLayout,
        });
        _main.ShowHome();
    }
}
