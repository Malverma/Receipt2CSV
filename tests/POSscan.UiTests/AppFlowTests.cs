using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
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
    public async Task Manual_confirmation_walks_the_receipt_with_the_keyboard()
    {
        var (window, main, store, _) = Launch();
        await main.ImportAsync(FindUp("SUPERSTORE BATCH.pdf"));
        var home = Assert.IsType<HomeVm>(main.Current);
        home.Recent.Single(r => r.Title.EndsWith("total 220.56")).Open.Execute(null);
        var review = Assert.IsType<ReviewVm>(main.Current);
        Assert.False(review.IsManualOpen);
        Dispatcher.UIThread.RunJobs();

        var toggle = window.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "ManualToggle");
        Assert.Equal("Manual Confirmation", toggle.Content);
        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        var manual = Assert.IsType<ManualConfirmVm>(review.Manual);
        Assert.True(store.GetSettings().ManualConfirmation);
        Assert.Equal("Line 1 of 53", manual.Progress);
        Assert.StartsWith("02970080141  ", manual.Text);
        Assert.EndsWith("  1.99 x 1", manual.Text);
        Assert.True(manual.HasClip);
        Shot(window, "22-manual-first-line");

        // The box has the cursor, so a person can just press Enter, or type and press Enter.
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ManualBox");
        Assert.True(box.IsFocused);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Line 2 of 53", manual.Progress);
        Assert.StartsWith("02970080147  ", box.Text);
        Assert.EndsWith("  1.99 x 1", box.Text);
        Assert.True(box.IsFocused);

        // Type a change, then Enter.
        box.Text = "02970080147  2.49 x 1";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2.49m, review.Session.Receipt.Items.Single(i => i.RawCode == "02970080147").UnitPrice);
        Assert.Equal("Line 3 of 53", manual.Progress);

        // Something unreadable stays on the line and says why.
        box.Text = "nonsense";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(manual.HasError);
        Assert.Equal("Line 3 of 53", manual.Progress);
        Shot(window, "23-manual-error");

        manual.Previous.Execute(null);
        Assert.EndsWith("  2.49 x 1", manual.Text);
        box.Text = "02970080147  1.99 x 1";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        for (int guard = 0; guard < 60 && !manual.IsFinished; guard++)
        {
            var fixedText = manual.Text.Replace("06520090399", "06620090399").Replace("05620000502", "06620000502");
            box.Text = fixedText;
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(manual.HasError, manual.Error);
        }
        Assert.True(manual.IsFinished);
        Assert.Contains("totals match at 220.06", manual.FinishText);
        Shot(window, "24-manual-finished");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(review.IsManualOpen);
        Assert.True(review.CanExport);
        Shot(window, "25-manual-closed-ready");

        // The toggle stays on, so another receipt opens the popup, and a finished one doesn't.
        main.ShowHome();
        var next = Assert.IsType<HomeVm>(main.Current);
        next.Recent.Single(r => r.Title.EndsWith("total 380.46")).Open.Execute(null);
        var second = Assert.IsType<ReviewVm>(main.Current);
        Assert.True(second.IsManualOpen);
        second.Manual!.Close.Execute(null);
        Assert.False(second.IsManualOpen);
        Assert.True(second.ManualConfirmation);

        main.ShowHome();
        Assert.IsType<HomeVm>(main.Current).Recent.Single(r => r.Title.EndsWith("total 220.56")).Open.Execute(null);
        Assert.False(Assert.IsType<ReviewVm>(main.Current).IsManualOpen);

        store.Dispose();
        window.Close();
    }

    private static async Task<(Window Window, MainVm Main, PosStore Store, ReviewVm Review)> OpenBatchReceipt()
    {
        var (window, main, store, _) = Launch();
        await main.ImportAsync(FindUp("SUPERSTORE BATCH.pdf"));
        Assert.IsType<HomeVm>(main.Current).Recent.Single(r => r.Title.EndsWith("total 220.56")).Open.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return (window, main, store, Assert.IsType<ReviewVm>(main.Current));
    }

    [AvaloniaFact]
    public async Task Items_say_check_until_a_person_has_confirmed_or_changed_them()
    {
        var (window, _, store, review) = await OpenBatchReceipt();
        Assert.DoesNotContain(review.Rows, r => r.Status == "New");
        var row = review.Rows.First(r => r.Review?.State == LineState.Attention && r.Review.Item.RawCode == "02970080141");
        Assert.Equal("Check", row.Status);
        Assert.Equal("02970080141", row.Code);

        // Clicking on it isn't enough.
        review.Selected = row;
        Assert.Equal("Check", review.Rows.Single(r => r.Line == row.Line).Status);

        review.Confirm.Execute(null);
        Assert.False(review.HasDetailError, review.DetailError);
        var after = review.Rows.Single(r => r.Line == row.Line);
        Assert.Equal("Checked", after.Status);
        Assert.Equal(Avalonia.Media.Brush.Parse("#0B6B3A").ToString(), after.StatusFg.ToString());
        Shot(window, "30-review-checked");
        store.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_above_the_list_finds_items_and_enter_does_not_confirm_anything()
    {
        var (window, _, store, review) = await OpenBatchReceipt();
        int all = review.Rows.Count;
        var edits = review.Session.Record.Edits.Count;

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SearchBox");
        box.Focus();
        box.Text = "02970080141";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        var found = Assert.Single(review.Rows);
        Assert.Equal("02970080141", found.Review!.Item.RawCode);
        Assert.Same(found, review.Selected);
        Assert.Equal(LineState.Attention, found.Review.State); // Enter searched. It didn't press Confirm.
        Assert.Equal(edits, review.Session.Record.Edits.Count);
        Shot(window, "31-review-search");

        // The name finds it too, in any case, using the button.
        var name = found.Title.Split(' ')[0].ToLowerInvariant();
        review.SearchText = name;
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SearchButton").Command!.Execute(null);
        Assert.Contains(review.Rows, r => r.Review?.Item.RawCode == "02970080141");
        Assert.All(review.Rows, r => Assert.Contains(name, r.Title.ToLowerInvariant()));

        review.SearchText = "no such item";
        review.Search.Execute(null);
        Assert.Empty(review.Rows);
        Assert.True(review.HasNoResults);
        Assert.False(review.HasSelection);
        Shot(window, "32-review-search-empty");

        review.ClearSearch.Execute(null);
        Assert.Equal(all, review.Rows.Count);
        Assert.Equal("", review.SearchText);
        store.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_in_the_popup_jumps_to_an_item_without_saving_the_line_on_screen()
    {
        var (window, _, store, review) = await OpenBatchReceipt();
        review.ManualConfirmation = true;
        Dispatcher.UIThread.RunJobs();
        var manual = Assert.IsType<ManualConfirmVm>(review.Manual);
        var edits = review.Session.Record.Edits.Count;
        var start = manual.Progress;
        manual.Text = "edited but not confirmed";

        var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ManualSearchBox");
        search.Focus();
        search.Text = "02970080147";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("02970080147  ", manual.Text);
        Assert.NotEqual(start, manual.Progress);
        Assert.Equal(edits, review.Session.Record.Edits.Count);
        Assert.True(window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ManualBox").IsFocused);
        Shot(window, "33-manual-search");

        manual.SearchText = "no such item";
        manual.Search.Execute(null);
        Assert.True(manual.HasError);
        Assert.StartsWith("02970080147  ", manual.Text);
        store.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_file_can_be_created_from_the_popup_without_finishing_it()
    {
        var (window, main, store, review) = await OpenBatchReceipt();
        review.ManualConfirmation = true;
        Dispatcher.UIThread.RunJobs();
        var manual = Assert.IsType<ManualConfirmVm>(review.Manual);
        Assert.Equal("Create price file", review.ExportLabel);
        Shot(window, "34-manual-with-search");

        // The first click explains what happens. The second creates the file.
        manual.CreateFile.Execute(null);
        Assert.False(review.IsManualOpen);
        Assert.IsType<ReviewVm>(main.Current);
        Assert.Equal("Create file anyway", review.ExportLabel);
        Assert.Contains("Click again", review.FooterText);
        Shot(window, "35-review-create-anyway");

        Assert.True(review.Export.CanExecute(null));
        review.Export.Execute(null);
        var done = Assert.IsType<DoneVm>(main.Current);
        Assert.True(done.HasWarning);
        Assert.Empty(RasCsv.Read(done.FilePath).Errors);
        Assert.NotEmpty(RasCsv.Read(done.FilePath).Items);
        Shot(window, "36-done-unchecked");
        store.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_receipt_image_can_be_clicked_zoomed_and_resized()
    {
        var (window, main, store, _) = Launch();
        await main.ImportAsync(Pdf);
        var review = Assert.IsType<ReviewVm>(main.Current);
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<ReviewView>().Single();
        var host = view.FindControl<Grid>("ImageHost")!;
        var scroll = view.FindControl<ScrollViewer>("ImageScroll")!;
        var lines = review.Session.Record.Lines;

        Point OnWindow(int line) =>
            host.TranslatePoint(new Point(host.Bounds.Width / 2, (lines[line].Top + lines[line].Bottom) / 2.0 * review.ImageWidth / review.Session.Record.PageWidth), window)!.Value;
        void Click(Point p)
        {
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        // A two-line item: clicking its quantity line, under the name, still picks the item.
        var twoLine = review.Rows.First(r => r.Review?.Item is { HasPrefixQuantity: true } i && review.Rows.All(o => o.Line != i.SourceLine + 1));
        var other = review.Rows.First(r => r.Kind == RowKind.Item && r != twoLine);
        review.Selected = twoLine; // Scrolls it into view.
        review.Selected = other;
        Dispatcher.UIThread.RunJobs();
        Click(OnWindow(twoLine.Line + 1));
        Assert.Same(twoLine, review.Selected);
        Shot(window, "40-review-clicked-on-image");

        // The wheel zooms, keeping the spot under the pointer where it was.
        var pointer = OnWindow(twoLine.Line);
        var before = window.TranslatePoint(pointer, host)!.Value;
        double fracX = before.X / host.Bounds.Width, fracY = before.Y / host.Bounds.Height;
        window.MouseWheel(pointer, new Vector(0, 3));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Math.Pow(1.2, 3), review.Zoom, 3);
        var after = window.TranslatePoint(pointer, host)!.Value;
        Assert.Equal(fracX, after.X / host.Bounds.Width, 2);
        Assert.Equal(fracY, after.Y / host.Bounds.Height, 2);
        Assert.Same(twoLine, review.Selected); // Zooming isn't a click.
        Shot(window, "41-review-zoomed");

        // Dragging the bar right of the image widens its column, and the image grows to fill it.
        var splitter = view.GetVisualDescendants().OfType<GridSplitter>().First();
        double fitBefore = review.FitWidth, columnBefore = scroll.Bounds.Width;
        var grip = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        window.MouseDown(grip, MouseButton.Left);
        window.MouseMove(grip + new Point(120, 0));
        window.MouseUp(grip + new Point(120, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(columnBefore + 120, scroll.Bounds.Width, 0);
        Assert.Equal(fitBefore + 120, review.FitWidth, 0);
        Shot(window, "42-review-wider-image");
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
        store.SaveSettings(store.GetSettings() with { ManualConfirmation = true });
        s.Save.Execute(null);
        Assert.IsType<HomeVm>(main.Current);
        Assert.True(store.GetSettings().ManualConfirmation); // Saving settings doesn't turn the toggle off.
        Assert.Equal(30m, store.GetSettings().PriceChangePercent);
        Assert.True(store.GetSettings().ExportWeighed);
        store.Dispose();
        window.Close();
    }
}
