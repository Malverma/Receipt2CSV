using POSscan.Core;

namespace POSscan.Tests;

public class DollarMisreadTests
{
    private static ReceiptItem Parse(params string[] itemLines)
    {
        var lines = new[] { "21-GROCERY" }.Concat(itemLines).Concat(["SUBTOTAL 15.00", "TOTAL 15.00"]).Select(l => new OcrLine(l)).ToList();
        return SuperstoreParser.Parse(lines).Items.Single();
    }

    [Theory]
    [InlineData("3 @ 85.00 15.00", 3, 5.00, 15.00)]     // The example from the store.
    [InlineData("3 @ 85.00 ea 15.00", 3, 5.00, 15.00)]
    [InlineData("1 @ 85.00 5.00", 1, 5.00, 5.00)]
    [InlineData("3 @ 83.33 10.00", 3, 3.33, 10.00)]     // Divides with a rounded cent.
    [InlineData("2 @ 80.50 1.00", 2, 0.50, 1.00)]
    [InlineData("20 @ 85.00 100.00", 20, 5.00, 100.00)] // Price below the total, but dropping the 8 fits exactly.
    public void A_price_with_the_dollar_sign_read_as_8_comes_from_the_line_total(string line, int qty, double price, double total)
    {
        var item = Parse("(" + qty + ")06038314170 PCO MUSH MRJ", line);
        Assert.Equal((decimal)qty, item.Quantity);
        Assert.Equal((decimal)price, item.UnitPrice);
        Assert.Equal((decimal)total, item.LineTotal);
    }

    [Fact]
    public void The_correct_quantity_is_kept_when_the_printed_one_is_also_misread()
    {
        var item = Parse("(3)06038314170 PCO MUSH MRJ", "8 @ 85.00 15.00");
        Assert.Equal(3m, item.Quantity);
        Assert.Equal(5.00m, item.UnitPrice);
    }

    [Theory]
    [InlineData("3 @ $5.00 15.00", 5.00)]
    [InlineData("10 @ 8.00 80.00", 8.00)]   // A real price starting with 8.
    [InlineData("2 @ 85.00 170.00", 85.00)] // A real price of 85, since the total agrees.
    [InlineData("2 @ $4.00 6.00", 4.00)]    // Not this rule: lower than the total, so it stays flagged for a person.
    public void Other_prices_are_left_as_printed(string line, double price)
    {
        Assert.Equal((decimal)price, Parse("06038314170 PCO MUSH MRJ", line).UnitPrice);
    }

    // From the third receipt of the real batch: 3 @ $3.50 10.50, with the 1 of the total faded off the paper.
    [Theory]
    [InlineData("3 @ $3.50 0.50")]
    [InlineData("3 @ $3.50 1.50")]
    [InlineData("1 @ $9.99 0.99")]
    public void A_price_above_a_misread_total_is_kept_and_flagged_not_worked_out_from_the_total(string line)
    {
        var lines = new[] { "27-PRODUCE", "(3)06038303810 PCO CREMINI MRJ", line, "SUBTOTAL 10.50", "TOTAL 10.50" }
            .Select(l => new OcrLine(l)).ToList();
        var r = SuperstoreParser.Parse(lines);
        var item = r.Items.Single();
        var printed = decimal.Parse(line.Split(' ')[2].TrimStart('$'), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(printed, item.UnitPrice);
        Assert.Equal(printed, r.ActualUnitCost(item));
        Assert.Contains(Reconciler.Check(r).Issues, i => i.Code == "LINE_MATH" && i.SourceLine == item.SourceLine);
    }

    [Fact]
    public void A_misread_line_total_is_offered_as_a_fix_on_the_review_screen()
    {
        var lines = new[] { "27-PRODUCE", "(3)06038303810 PCO CREMINI MRJ", "3 @ $3.50 0.50", "SUBTOTAL 10.50", "TOTAL 10.50" }
            .Select(l => new OcrLine(l)).ToList();
        var r = SuperstoreParser.Parse(lines);
        var review = Review.Build(r, Reconciler.Check(r), new DraftDecisions(), new ItemMatcher([]), _ => new PriceLookup(null, null, false), new ReviewSettings());
        var line = review.Lines.Single();
        Assert.Equal(LineState.Attention, line.State);
        Assert.Equal(3.50m, line.Cost);
        Assert.Contains(line.Fixes, f => f.Label == "Use 10.50");
    }

    [Fact]
    public void A_weighed_price_with_the_dollar_sign_read_as_8_is_corrected()
    {
        var item = Parse("4556 CABBAGE GREEN MRJ", "2.695 kg @ 82.84/kg 7.65");
        Assert.Equal(2.84m, item.UnitPrice);
        Assert.Equal(2.695m, item.Quantity);
    }

    [Fact]
    public void Under_a_kilo_the_price_is_properly_more_than_the_total()
    {
        Assert.Equal(2.84m, Parse("4556 CABBAGE GREEN MRJ", "0.500 kg @ $2.84/kg 1.42").UnitPrice);
    }
}

public class ReceiptTests
{
    private static ParsedReceipt Fixture() => SuperstoreParser.Parse(
        File.ReadAllLines(Path.Combine("fixtures", "superstore-ocr.txt")).Select(l => new OcrLine(l)).ToList());

    private static ReceiptItem Item(ParsedReceipt r, string code) => r.Items.Single(i => i.RawCode == code);

    [Fact]
    public void Card_slip_and_address_are_not_items()
    {
        var r = Fixture();
        Assert.DoesNotContain(r.Items, i => i.RawCode == "757");
        Assert.Equal("(3)06870011200", r.Items[0].RawText.Split(' ')[0]);
    }

    [Fact]
    public void All_lines_on_the_receipt_are_accounted_for()
    {
        var r = Fixture();
        Assert.Empty(r.Unparsed);
        Assert.Equal(24, r.Items.Count(i => !i.IsNonSale));
        Assert.Equal(new DateOnly(2026, 10, 27), r.Date);
    }

    [Fact]
    public void Printed_totals_are_read()
    {
        var r = Fixture();
        Assert.Equal(179.79m, r.Subtotal);
        Assert.Equal(53.00m, r.TaxBase);
        Assert.Equal(2.65m, r.Tax);
        Assert.Equal(182.44m, r.Total);
        Assert.Equal(45, r.ItemCount);
    }

    [Fact]
    public void Fees_stay_out_of_cost()
    {
        var milk = Item(Fixture(), "06870011200");
        Assert.Equal(0.36m, milk.Fees);
        Assert.Equal(5.05m, Fixture().ActualUnitCost(milk));
    }

    [Fact]
    public void Weighed_items_keep_price_per_kg()
    {
        var r = Fixture();
        var cabbage = Item(r, "4556");
        Assert.Equal(PriceUnit.Kg, cabbage.Unit);
        Assert.Equal(2.540m, cabbage.Quantity);
        Assert.Equal(2.16m, r.ActualUnitCost(cabbage));
        Assert.Equal(5.49m, cabbage.LineTotal);
    }

    [Fact]
    public void Multi_buy_cost_is_what_was_paid()
    {
        var r = Fixture();
        var parfait = Item(r, "62883487114");
        Assert.Equal(4m, parfait.Quantity);
        Assert.Equal(3.00m, r.ActualUnitCost(parfait));
        Assert.Equal(3.50m, r.Groups.Single().RegularPrice);
        Assert.Equal(33.00m, r.Groups.Single().Total);
    }

    [Fact]
    public void A_printed_discount_is_ignored_and_the_price_before_it_is_the_cost()
    {
        var r = Fixture();
        var item = Item(r, "2439450");
        Assert.Equal(-0.50m, item.Discount);
        Assert.Equal(3.50m, r.ActualUnitCost(item));
    }

    [Fact]
    public void Misread_quantity_is_corrected_by_the_second_printed_quantity()
    {
        // OCR reads "9 @ $2.99 8.97". The prefix says (3), and 3 x 2.99 = 8.97.
        Assert.Equal(3m, Item(Fixture(), "06038307757").Quantity);
    }

    [Fact]
    public void Misread_total_blocks_export_and_suggests_the_calculated_value()
    {
        var r = Fixture();
        var check = Reconciler.Check(r);
        Assert.False(check.CanExport);
        Assert.Contains(check.Issues, i => i.Code == "SUBTOTAL_GAP" && i.Severity == Severity.Block);
        Assert.Contains(check.Issues, i => i.Code == "LINE_MATH" && i.Suggestion == "8.00");
        Assert.Equal(6.00m, Item(r, "62796300068").LineTotal); // Suggested, not silently applied.
        var fix = Assert.Single(check.GapFixes);
        Assert.Equal(8.00m, fix[0].To);
    }

    [Fact]
    public void After_confirming_the_fix_the_real_receipt_reconciles_exactly()
    {
        var r = Fixture();
        Item(r, "62796300068").LineTotal = 8.00m;
        var check = Reconciler.Check(r);
        Assert.Equal(179.79m, check.CalculatedSubtotal);
        Assert.Equal(45, check.CalculatedItemCount);
        Assert.DoesNotContain(check.Issues, i => i.Severity == Severity.Block);
        Assert.DoesNotContain(check.Issues, i => i.Code is "TAX_BASE" or "TAX_MATH");
        Assert.True(check.CanExport);
    }

    [Fact]
    public void Codes_that_cannot_be_real_are_flagged()
    {
        var check = Reconciler.Check(Fixture());
        var flagged = check.Issues.Where(i => i.Code == "BAD_CODE").Select(i => i.Message).ToList();
        Assert.Contains(flagged, m => m.Contains("06870012.00"));
        Assert.Contains(flagged, m => m.Contains("785391/0635"));
        Assert.Contains(flagged, m => m.Contains("2930701501"));
    }

    [Fact]
    public void Look_alike_digit_is_suggested_for_the_2_cent_gap_and_not_applied()
    {
        // The scan's "3.98" can read as "3.96". Seed that error to reproduce the gap found by hand.
        var r = Fixture();
        Item(r, "62796300068").LineTotal = 8.00m;
        var line = Item(r, "06038300291");
        line.LineTotal = 3.96m;
        line.UnitPrice = 3.96m;

        var check = Reconciler.Check(r);

        Assert.False(check.CanExport);
        Assert.Contains(check.GapFixes, f => f.Count == 1 && f[0].SourceLine == line.SourceLine && f[0].To == 3.98m);
        Assert.Equal(3.96m, line.LineTotal);
    }

    [Fact]
    public void A_receipt_with_no_items_cannot_export()
    {
        var check = Reconciler.Check(SuperstoreParser.Parse([new OcrLine("not a receipt")]));
        Assert.False(check.CanExport);
    }

    [Fact]
    public void Unreadable_item_area_lines_block_export()
    {
        var r = SuperstoreParser.Parse([
            new("22 -DAIRY"), new("06038314170 PCO MUSH MRJ 3.50"), new("%%% smudge %%%"),
            new("SUBTOTAL 3.50"), new("TOTAL 3.50"), new("Number of Items: 1"),
        ]);
        var check = Reconciler.Check(r);
        Assert.Contains(check.Issues, i => i.Code == "UNPARSED");
        Assert.False(check.CanExport);
    }
}
