using POSscan.Core;

namespace POSscan.Tests;

public class ManualConfirmationTests
{
    private static List<OcrLine> L(params string[] t) => t.Select(x => new OcrLine(x)).ToList();

    private static readonly List<OcrLine> Receipt = L(
        "22-DAIRY", "(4)06870011200 HOMO MILK 3.25% RQ", "4 @ $5.05 20.20", "RECYCLING FEE", "4@$0.02 0.08",
        "(2)05680051124 OIKO YOG CA MO GMRJ", "$4.00 ea or 2/$6.00", "2 @ 2/$6.00 6.00",
        "06038314170 PCO MUSH MRJ 3.50", "PM BOGO Set Price MRJ -0.50",
        "4556 CABBAGE GREEN MRJ", "2.695 kg @ $2.84/kg 7.65",
        "24632 PHY STAMP EARN", "8 @ $0.00",
        "SUBTOTAL 36.93", "G=GST 5% 6.00 @ 5.000% 0.30", "TOTAL 37.23");

    private static (ParsedReceipt R, DraftDecisions D) Build(params ReceiptEdit[] edits) => ReceiptDraft.Build(Receipt, edits);

    private static ConfirmStep StepFor(ParsedReceipt r, DraftDecisions d, string code) =>
        ManualConfirmation.Steps(r, d).First(s => s.Kind == ConfirmKind.Item && r.Items.Single(i => i.SourceLine == s.Line).RawCode == code);

    [Fact]
    public void Steps_go_down_the_receipt_in_order_and_end_with_the_totals()
    {
        var (r, d) = Build();
        var kinds = ManualConfirmation.Steps(r, d).Select(s => s.Kind).ToList();
        Assert.Equal(
            [ConfirmKind.Item, ConfirmKind.Fees, ConfirmKind.Item, ConfirmKind.GroupTotal, ConfirmKind.Item, ConfirmKind.Discount, ConfirmKind.Item,
             ConfirmKind.Subtotal, ConfirmKind.Tax, ConfirmKind.Total], kinds);
    }

    [Fact]
    public void Lines_show_their_numbers_the_way_the_receipt_does()
    {
        var (r, d) = Build();
        Assert.Equal("06870011200  4 @ 5.05", ManualConfirmation.Format(r, StepFor(r, d, "06870011200")));
        Assert.Equal("05680051124  2", ManualConfirmation.Format(r, StepFor(r, d, "05680051124")));
        Assert.Equal("4556  2.695 @ 2.84", ManualConfirmation.Format(r, StepFor(r, d, "4556")));
    }

    [Fact]
    public void Pressing_enter_on_every_line_unchanged_edits_nothing()
    {
        var (r, d) = Build();
        foreach (var s in ManualConfirmation.Steps(r, d))
        {
            var input = ManualConfirmation.Read(r, s, ManualConfirmation.Format(r, s));
            Assert.Null(input.Error);
            Assert.Empty(input.Edits);
        }
    }

    [Fact]
    public void Changing_a_price_updates_the_line_total_too()
    {
        var (r, d) = Build();
        var s = StepFor(r, d, "06870011200");
        var input = ManualConfirmation.Read(r, s, "06870011200 4 @ 5.15");
        Assert.Null(input.Error);
        var (after, _) = Build([.. input.Edits]);
        var milk = after.Items.Single(i => i.RawCode == "06870011200");
        Assert.Equal(5.15m, milk.UnitPrice);
        Assert.Equal(20.60m, milk.LineTotal);
    }

    [Fact]
    public void Changing_a_single_item_price_changes_price_and_total_together()
    {
        var (r, d) = Build();
        var input = ManualConfirmation.Read(r, StepFor(r, d, "06038314170"), "06038314170 1 @ 3.99");
        var (after, _) = Build([.. input.Edits]);
        var mush = after.Items.Single(i => i.RawCode == "06038314170");
        Assert.Equal(3.99m, mush.UnitPrice);
        Assert.Equal(3.99m, mush.LineTotal);
    }

    [Fact]
    public void A_corrected_code_and_quantity_are_both_applied()
    {
        var (r, d) = Build();
        var input = ManualConfirmation.Read(r, StepFor(r, d, "06870011200"), "06870011500  3 @ $5.05");
        var (after, _) = Build([.. input.Edits]);
        var milk = after.Items.Single(i => i.RawCode == "06870011500");
        Assert.Equal(3m, milk.Quantity);
        Assert.Equal(15.15m, milk.LineTotal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("06870011200")]
    [InlineData("06870011200 4")]
    [InlineData("06870011200 four @ 5.05")]
    [InlineData("06870011200 0 @ 5.05")]
    [InlineData("06870011200 4 @ abc")]
    [InlineData("06870011200 4 @ -5.05")]
    [InlineData("06870011200 4 @ 5.05 20.20")]
    [InlineData("0687oo11200x 4 @ 5.05")]
    public void Bad_input_is_explained_and_changes_nothing(string text)
    {
        var (r, d) = Build();
        var input = ManualConfirmation.Read(r, StepFor(r, d, "06870011200"), text);
        Assert.NotNull(input.Error);
        Assert.Empty(input.Edits);
    }

    [Fact]
    public void A_printed_total_that_does_not_follow_is_warned_about_and_replaced()
    {
        var lines = L("27-PRODUCE", "(2)62796300068 BASIL 6IN MRJ", "2 @ $4.00 6.00", "SUBTOTAL 8.00", "TOTAL 8.00");
        var (r, d) = ReceiptDraft.Build(lines, []);
        var s = ManualConfirmation.Steps(r, d)[0];
        Assert.Contains("6.00", ManualConfirmation.Warning(r, s));
        var (after, _) = ReceiptDraft.Build(lines, ManualConfirmation.Read(r, s, ManualConfirmation.Format(r, s)).Edits);
        Assert.Equal(8.00m, after.Items.Single().LineTotal);
    }

    [Fact]
    public void A_missing_price_can_be_typed_in()
    {
        var lines = L("27-PRODUCE", "(3)06038303814 PCO MUSH MRJ", "3 @ $3.50 10.5:", "SUBTOTAL 10.50", "TOTAL 10.50");
        var (r, d) = ReceiptDraft.Build(lines, []);
        var s = ManualConfirmation.Steps(r, d)[0];
        Assert.Equal("06038303814  3 @ ", ManualConfirmation.Format(r, s));
        Assert.NotNull(ManualConfirmation.Read(r, s, "06038303814 3 @").Error);
        var (after, _) = ReceiptDraft.Build(lines, ManualConfirmation.Read(r, s, "06038303814 3 @ 3.50").Edits);
        Assert.Equal(10.50m, after.Items.Single().LineTotal);
    }

    [Fact]
    public void Fees_discounts_group_totals_and_receipt_totals_can_all_be_corrected()
    {
        var (r, d) = Build();
        var steps = ManualConfirmation.Steps(r, d);
        ReceiptEdit Edit(ConfirmKind kind, string text) => Assert.Single(ManualConfirmation.Read(r, steps.First(s => s.Kind == kind), text).Edits);
        var (after, _) = Build(
            Edit(ConfirmKind.Fees, "0.10"), Edit(ConfirmKind.Discount, "-0.75"), Edit(ConfirmKind.GroupTotal, "7.00"),
            Edit(ConfirmKind.Subtotal, "$40.00"), Edit(ConfirmKind.Tax, "0.35"), Edit(ConfirmKind.Total, "40.35"));
        Assert.Equal(0.10m, after.Items.Single(i => i.RawCode == "06870011200").Fees);
        Assert.Equal(-0.75m, after.Items.Single(i => i.RawCode == "06038314170").Discount);
        Assert.Equal(7.00m, after.Groups.Single().Total);
        Assert.Equal((40.00m, 0.35m, 40.35m), (after.Subtotal, after.Tax, after.Total));
    }

    [Theory]
    [InlineData(ConfirmKind.Total, "abc")]
    [InlineData(ConfirmKind.Total, "-1.00")]
    [InlineData(ConfirmKind.Discount, "0.50")]
    [InlineData(ConfirmKind.Total, "")]
    public void Bad_single_numbers_are_explained(ConfirmKind kind, string text)
    {
        var (r, d) = Build();
        Assert.NotNull(ManualConfirmation.Read(r, ManualConfirmation.Steps(r, d).First(s => s.Kind == kind), text).Error);
    }

    [Fact]
    public void A_skipped_line_is_left_out()
    {
        var (r, _) = Build();
        var milk = r.Items.Single(i => i.RawCode == "06870011200").SourceLine;
        var (after, d) = Build(new ReceiptEdit(EditKind.Skip, milk));
        Assert.DoesNotContain(ManualConfirmation.Steps(after, d), s => s.Line == milk);
    }

    [Fact]
    public void A_coupon_line_can_stay_negative()
    {
        var lines = L("49-OTHER", "41000007106 FREE P&G GPMJ -25.00", "SUBTOTAL -25.00", "TOTAL -25.00");
        var (r, d) = ReceiptDraft.Build(lines, []);
        var s = ManualConfirmation.Steps(r, d)[0];
        var input = ManualConfirmation.Read(r, s, ManualConfirmation.Format(r, s));
        Assert.Null(input.Error);
        Assert.Empty(input.Edits);
    }
}
