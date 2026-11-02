using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using POSscan.App;
using POSscan.App.ViewModels;
using POSscan.App.Views;
using POSscan.Core;
using POSscan.Data;
using POSscan.Ocr;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(POSscan.UiTests.TestApp))]

namespace POSscan.UiTests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App.App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class StubShell : IShell
{
    public string? OpenedFolder { get; private set; }
    public Task<string?> PickPdfAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public void OpenFolder(string path) => OpenedFolder = path;
}

/// <summary>Drives the real screens with the real OCR on the real receipt, and saves what each screen looks like.</summary>
public class AppFlowTests
{
    private static readonly string Pdf = FindUp("superstore reciept.pdf");
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("POSSCAN_SHOTS") ?? Path.Combine(Path.GetTempPath(), "posscan-shots");

    private static string FindUp(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, name))) return Path.Combine(d.FullName, name);
        throw new FileNotFoundException(name);
    }

    private static void Shot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ShotDir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, name + ".png"));
    }

    private static (Window Window, MainVm Main, PosStore Store, string Dir) Launch()
    {
        var dir = Directory.CreateTempSubdirectory("posscan-ui").FullName;
        var store = new PosStore(Path.Combine(dir, "data"));
        store.SaveSettings(new AppSettings { OutputFolder = Path.Combine(dir, "out") });
        var main = new MainVm(new Workflow(store, new ReceiptReader()), new StubShell());
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 800 };
        window.Show();
        return (window, main, store, dir);
    }

    private static void FixCode(ReviewVm vm, string bad, string good)
    {
        vm.Selected = vm.Rows.Single(r => r.Review?.Item.RawCode == bad);
        vm.CodeText = good;
        vm.Confirm.Execute(null);
        Assert.False(vm.HasDetailError, vm.DetailError);
        Assert.NotEqual(LineState.Attention, vm.Rows.Single(r => r.Review?.Item.RawCode == good).Review!.State);
    }

    [AvaloniaFact]
    public async Task Whole_flow_on_the_real_receipt()
    {
        var (window, main, store, _) = Launch();
        Assert.IsType<HomeVm>(main.Current);
        Shot(window, "1-home-empty");

        await main.ImportAsync(Pdf);
        var review = Assert.IsType<ReviewVm>(main.Current);
        Assert.False(review.CanExport);
        Assert.Equal("Totals off by 2.00", review.TotalsChip);
        Assert.Equal(RowKind.Totals, review.Rows[0].Kind);
        Assert.Contains("from 6.00 to 8.00", Assert.Single(review.Fixes).Label); // Offered right where the problem shows.
        Shot(window, "2-review-totals-off");

        // The totals row offers no guess here, so the person opens the flagged line.
        var basil = review.Rows.Single(r => r.Review?.Item.RawCode == "62796300068");
        review.Selected = basil;
        Shot(window, "3-review-line-math");
        var fix = Assert.Single(review.Fixes);
        Assert.Equal("Use 8.00", fix.Label);
        fix.Run.Execute(null);
        Assert.Equal("Totals match: 179.79", review.TotalsChip);

        FixCode(review, "06870012.00", "06870012500");
        FixCode(review, "€2930701501", "62930701501");
        FixCode(review, "785391/0635", "78539170635");
        FixCode(review, "06036314170", "06038314170");

        // In-store codes have no UPC, so POSscan must ask for a SKU rather than export them blank.
        review.Selected = review.Rows.Single(r => r.Review?.Item.RawCode == "2439450");
        review.Confirm.Execute(null);
        Assert.True(review.HasDetailError);
        Shot(window, "4-review-needs-sku");
        review.SkuText = "2439450";
        review.Confirm.Execute(null);
        Assert.False(review.HasDetailError);

        foreach (var code in new[] { "2439460", "2439480", "2439490" })
        {
            review.Selected = review.Rows.Single(r => r.Review?.Item.RawCode == code);
            review.SkuText = code;
            review.Confirm.Execute(null);
        }
        Shot(window, "5-review-new-item");

        // Keyboard path: Confirm moves to the next line by itself.
        for (int guard = 0; guard < 40 && review.Rows.Any(r => r.Review?.State == LineState.Attention); guard++)
        {
            Assert.Equal(LineState.Attention, review.Selected!.Review!.State);
            review.Confirm.Execute(null);
            Assert.False(review.HasDetailError, $"{review.DetailError} code=[{review.CodeText}] selected=[{review.Selected?.Review?.Item.RawCode}] state={review.Selected?.Review?.State} rows=" + string.Join(",", review.Rows.Where(r => r.Review?.State == LineState.Attention).Select(r => r.Review!.Item.RawCode)));
        }
        Assert.True(review.CanExport);
        Shot(window, "6-review-ready");

        review.Export.Execute(null);
        var done = Assert.IsType<DoneVm>(main.Current);
        Assert.Equal(2, done.ByHand.Count);
        var file = RasCsv.Read(done.FilePath);
        Assert.Empty(file.Errors);
        Assert.Equal(22, file.Items.Count);
        Assert.Equal(3.98m, file.Items.Single(i => i.Upc == Upc.ReceiptCodeToRasUpc("06038300291")).NewPrice);
        Shot(window, "7-done");

        done.MarkImported.Execute(null);
        var home = main.ShowHome();
        Shot(window, "8-home-recent");
        Assert.Single(home.Recent);

        main.ShowSettings();
        Shot(window, "9-settings");

        // Adding the same PDF again is caught, and reopening shows the finished receipt.
        await main.ImportAsync(Pdf);
        home = Assert.IsType<HomeVm>(main.Current);
        Assert.True(home.HasDuplicate);
        Shot(window, "10-home-duplicate");
        home.OpenDuplicate.Execute(null);
        Assert.IsType<DoneVm>(main.Current);

        store.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_stack_of_receipts_lists_each_one_on_the_home_screen()
    {
        var (window, main, store, _) = Launch();

        await main.ImportAsync(FindUp("SUPERSTORE BATCH.pdf"));

        var home = Assert.IsType<HomeVm>(main.Current);
        Assert.Contains("Found 3 receipts", home.Notice);
        Assert.Equal(3, home.Recent.Count);
        Assert.Contains(home.Recent, r => r.Title.EndsWith("total 380.46"));
        Assert.Contains(home.Recent, r => r.Title.EndsWith("total 220.56"));
        Assert.Contains(home.Recent, r => r.Title.EndsWith("total not read"));
        Shot(window, "20-home-stack");

        home.Recent.Single(r => r.Title.EndsWith("total 220.56")).Open.Execute(null);
        Assert.IsType<ReviewVm>(main.Current);
        Shot(window, "21-review-flipped-page");
        store.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Bad_file_returns_home_with_a_clear_message()
    {
        var (window, main, store, dir) = Launch();
        var bad = Path.Combine(dir, "broken.pdf");
        File.WriteAllText(bad, "this is not a pdf");

        await main.ImportAsync(bad);

        var home = Assert.IsType<HomeVm>(main.Current);
        Assert.True(home.HasError);
        Assert.Contains("couldn't be read", home.Error);
        Shot(window, "11-home-error");
        store.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dropping_something_that_is_not_a_pdf_is_explained()
    {
        var (window, main, store, _) = Launch();
        var home = Assert.IsType<HomeVm>(main.Current);
        await home.DropAsync("photo.jpg");
        Assert.True(home.HasError);
        store.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_reject_bad_input_and_save_good_input()
    {
        var (window, main, store, _) = Launch();
        main.ShowSettings();
        var s = Assert.IsType<SettingsVm>(main.Current);
        s.Percent = "lots";
        s.Save.Execute(null);
        Assert.True(s.HasError);
        Assert.IsType<SettingsVm>(main.Current);

        s.Percent = "30";
        s.ExportWeighed = true;
        s.Save.Execute(null);
        Assert.IsType<HomeVm>(main.Current);
        Assert.Equal(30m, store.GetSettings().PriceChangePercent);
        Assert.True(store.GetSettings().ExportWeighed);
        store.Dispose();
        window.Close();
    }
}
