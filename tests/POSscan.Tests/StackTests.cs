using POSscan.Core;

namespace POSscan.Tests;

/// <summary>Three real receipts scanned into one PDF: one slanted, one upside down, and one cut off partway.</summary>
public class StackTests
{
    private static List<OcrLine> Lines(int n) =>
        File.ReadAllLines(Path.Combine("fixtures", $"stack-receipt-{n}-ocr.txt")).Select(l => new OcrLine(l)).ToList();

    private static ParsedReceipt Parse(int n) => SuperstoreParser.Parse(Lines(n));

    private static ReceiptItem Item(ParsedReceipt r, string code) => r.Items.Single(i => i.RawCode == code);

    [Theory]
    [InlineData(1, 380.46)]
    [InlineData(2, 220.06)]
    public void Receipts_add_up_to_their_printed_subtotal(int n, decimal subtotal)
    {
        var r = Parse(n);
        var check = Reconciler.Check(r);
        Assert.Equal(subtotal, r.Subtotal);
        Assert.Equal(subtotal, check.CalculatedSubtotal);
        Assert.Empty(r.Unparsed);
        Assert.True(check.CanExport);
    }

    [Fact]
    public void A_trimmed_item_count_is_only_a_warning_when_the_subtotal_matches()
    {
        var issue = Reconciler.Check(Parse(2)).Issues.Single(i => i.Code == "NO_COUNT");
        Assert.Equal(Severity.Warning, issue.Severity);
    }

    [Fact]
    public void A_cut_off_receipt_stays_blocked_and_says_why()
    {
        var check = Reconciler.Check(Parse(3));
        Assert.False(check.CanExport);
        Assert.Contains(check.Issues, i => i.Code == "NO_SUBTOTAL" && i.Message.Contains("bottom of this receipt"));
    }

    [Fact]
    public void A_coupon_counts_toward_the_subtotal_but_is_not_an_item()
    {
        var coupon = Item(Parse(1), "41000007106");
        Assert.Equal(-25m, coupon.LineTotal);
        Assert.True(coupon.IsNonSale);
    }

    [Fact]
    public void Tax_flags_on_their_own_row_stay_with_the_item()
    {
        var r = Parse(1);
        Assert.Equal("GPMRJ", Item(r, "03077225515").TaxFlags);
        Assert.Equal(25m, Item(r, "03077225515").LineTotal);
    }

    [Fact]
    public void A_sale_price_with_a_limit_is_read_as_the_price_paid()
    {
        var r = Parse(1);
        var cream = Item(r, "06870026700");
        Assert.Equal(3m, cream.Quantity);
        Assert.Equal(3.79m, r.ActualUnitCost(cream));
    }

    [Fact]
    public void One_sale_price_across_several_items_is_a_group()
    {
        var r = Parse(2);
        var soup = Item(r, "06320912635");
        var group = r.Groups.Single(g => g.Id == soup.GroupId);
        Assert.Equal(3m, group.Quantity);
        Assert.Equal(9.87m, group.Total);
        Assert.Equal(3.79m, group.RegularPrice);
        Assert.Equal(3.29m, r.ActualUnitCost(soup));
    }

    [Fact]
    public void Units_past_a_deal_limit_join_the_deal_and_are_noted()
    {
        var r = Parse(2);
        var mix = Item(r, "06620000498");
        var group = r.Groups.Single(g => g.Id == mix.GroupId);
        Assert.Equal(6m, group.Quantity);
        Assert.Equal(7.98m, group.Total);
        Assert.Equal(2m, group.ExtraQuantity);
        Assert.Equal(1.49m, r.ActualUnitCost(mix));
        var check = Reconciler.Check(r);
        Assert.DoesNotContain(check.Issues, i => i.Code == "GROUP_MATH");
        Assert.Contains(check.Issues, i => i.Code == "MIXED_PRICE" && i.Severity == Severity.Warning);
    }

    [Fact]
    public void A_second_quantity_line_on_one_item_uses_the_higher_price()
    {
        var lines = new[]
        {
            "22-DAIRY", "(4)06870026700 DAIRYLND SOUR CR MRJ", "$3.79 1mt 3, $4.49 ea", "3 @ $3.79 ea 11.37", "1 @ $4.49 ea 4.49",
            "SUBTOTAL 15.86", "TOTAL 15.86",
        }.Select(l => new OcrLine(l)).ToList();
        var r = SuperstoreParser.Parse(lines);
        var cream = Item(r, "06870026700");
        Assert.Equal(4m, cream.Quantity);
        Assert.Equal(4.49m, r.ActualUnitCost(cream));
        Assert.Equal(15.86m, Reconciler.Check(r).CalculatedSubtotal);
    }

    [Theory]
    [InlineData("RECYCLING FEE 40$0.02 0.08")]
    [InlineData("RECYCLING FEE 0.08")]
    [InlineData("DEPOSIT 1 0.08")]
    public void A_fee_on_one_row_is_still_a_fee(string feeLine)
    {
        var lines = new[]
        {
            "22-DAIRY", "(4)06870011200 HOMO MILK 3.25% RQ", "4 @ $5.05 20.20", feeLine, "4@$0.02",
            "SUBTOTAL 20.28", "TOTAL 20.28",
        };
        var r = SuperstoreParser.Parse(lines.Where(l => feeLine != "RECYCLING FEE 40$0.02 0.08" || l != "4@$0.02").Select(l => new OcrLine(l)).ToList());
        Assert.Empty(r.Unparsed);
        Assert.Equal(0.08m, Item(r, "06870011200").Fees);
        Assert.Equal(5.05m, r.ActualUnitCost(Item(r, "06870011200")));
    }

    [Fact]
    public void Text_showing_through_from_the_back_does_not_hide_a_quantity_line()
    {
        var lines = new[] { "22-DAIRY", "(2)05680024917 ACTIVIA MRJ", "2 @ $3.50 ea CAR 1 7.00", "SUBTOTAL 7.00", "TOTAL 7.00" }
            .Select(l => new OcrLine(l)).ToList();
        var r = SuperstoreParser.Parse(lines);
        Assert.Empty(r.Unparsed);
        Assert.Equal(7m, Item(r, "05680024917").LineTotal);
    }

    // ---- Finding where each receipt starts ----

    private static List<OcrLine> L(params string[] text) => text.Select(t => new OcrLine(t)).ToList();

    [Fact]
    public void Each_page_with_a_header_is_its_own_receipt()
    {
        var parts = ReceiptSplitter.Split([Lines(1), Lines(2), Lines(3)]);
        Assert.Equal(3, parts.Count);
        Assert.Equal([0, 1, 2], parts.Select(p => p.Single().Page));
    }

    [Fact]
    public void A_page_with_no_header_continues_the_receipt_before_it()
    {
        var parts = ReceiptSplitter.Split([L("REAL CANADIAN", "SUPERSTORE", "21-GROCERY", "06038300427 TISSUE MRJ 15.00"), L("06038306101 TOWEL MRJ 13.00", "SUBTOTAL 28.00")]);
        Assert.Equal([0, 1], parts.Single().Select(p => p.Page));
    }

    [Fact]
    public void Two_receipts_on_one_long_page_are_split_at_the_second_header()
    {
        var parts = ReceiptSplitter.Split([L(
            "SUPERSTORE", "21-GROCERY", "06038300427 TISSUE MRJ 15.00", "SUBTOTAL 15.00", "TOTAL 15.00",
            "SUPERSTORE", "21-GROCERY", "06038306101 TOWEL MRJ 13.00", "SUBTOTAL 13.00", "TOTAL 13.00")]);
        Assert.Equal(2, parts.Count);
        Assert.StartsWith("SUPERSTORE", parts[1].Single().Lines[0].Text);
        Assert.Equal(5, parts[1].Single().Lines.Count);
    }

    [Fact]
    public void A_footer_naming_the_store_does_not_start_a_receipt()
    {
        var parts = ReceiptSplitter.Split([L(
            "SUPERSTORE", "21-GROCERY", "06038300427 TISSUE MRJ 15.00", "SUBTOTAL 15.00", "TOTAL 15.00", "Thanks for shopping at Real Canadian Superstore")]);
        Assert.Single(parts);
        Assert.Equal(6, parts[0].Single().Lines.Count);
    }

    [Fact]
    public void A_card_slip_above_the_header_stays_with_its_receipt()
    {
        var slip = Enumerable.Range(0, 20).Select(i => $"slip line {i}").ToArray();
        var parts = ReceiptSplitter.Split([L([.. slip, "SUPERSTORE", "21-GROCERY", "06038300427 TISSUE MRJ 15.00", "SUBTOTAL 15.00"])]);
        Assert.Single(parts);
    }

    [Fact]
    public void Handwriting_above_the_logo_does_not_start_a_receipt_of_its_own()
    {
        var parts = ReceiptSplitter.Split([L("Cash", "$400.00", "SUPERSTORE", "21-GROCERY", "06038300427 TISSUE MRJ 15.00", "SUBTOTAL 15.00")]);
        Assert.Single(parts);
        Assert.Equal("Cash", parts[0].Single().Lines[0].Text);
    }
}
