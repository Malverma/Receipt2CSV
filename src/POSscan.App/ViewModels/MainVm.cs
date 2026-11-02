using POSscan.Data;

namespace POSscan.App.ViewModels;

public sealed class MainVm : Vm
{
    private object _current = null!;
    public Workflow Workflow { get; }
    public IShell Shell { get; }

    public object Current { get => _current; private set => Set(ref _current, value); }

    public MainVm(Workflow workflow, IShell shell)
    {
        Workflow = workflow;
        Shell = shell;
        ShowHome();
    }

    public HomeVm ShowHome(string? error = null, (string Path, long Id)? duplicate = null)
    {
        var home = new HomeVm(this) { Error = error };
        if (duplicate is { } d) home.ShowDuplicate(d.Path, d.Id);
        Current = home;
        return home;
    }

    public void ShowSettings() => Current = new SettingsVm(this);

    public async Task ImportAsync(string path, bool allowDuplicate = false)
    {
        Current = new BusyVm(Path.GetFileName(path));
        try
        {
            var outcome = await Task.Run(() => Workflow.Import(path, allowDuplicate));
            if (outcome.Added.Count > 1) ShowHome().Notice = $"Found {outcome.Added.Count} receipts in {Path.GetFileName(path)}. Open each one below to review it.";
            else if (outcome.ReceiptId is { } id) OpenReceipt(id);
            else ShowHome(duplicate: (path, outcome.DuplicateOf!.Value));
        }
        catch (ReceiptReadException ex)
        {
            if (ex.InnerException is not null) Log.Error("Import", ex.InnerException);
            ShowHome(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Import", ex);
            ShowHome("Something went wrong while reading that receipt. Your other receipts are safe.");
        }
    }

    public void OpenReceipt(long id)
    {
        var record = Workflow.Store.GetReceipt(id);
        if (record.Status is ReceiptStatus.Exported or ReceiptStatus.Imported && Workflow.Store.LatestBatch(id) is { } batch)
            Current = DoneVm.FromBatch(this, record, batch);
        else
            Current = new ReviewVm(this, Workflow.Open(id));
    }

    public void ShowDone(ReceiptRecord record, ExportOutcome outcome) => Current = new DoneVm(this, record, outcome);
}

public sealed record BusyVm(string FileName);
