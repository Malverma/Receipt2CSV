namespace POSscan.Core;

/// <summary>Checks parsed values against the receipt's own printed totals. Suggests fixes and never applies them.</summary>
public static class Reconciler
{
    private static readonly Dictionary<char, char[]> LookAlikes = new()
    {
        ['0'] = ['8', '6', '9'], ['1'] = ['7'], ['3'] = ['8', '5'], ['5'] = ['6', '3', '8'],
        ['6'] = ['5', '8', '0'], ['7'] = ['1'], ['8'] = ['3', '6', '0', '9'], ['9'] = ['8', '0'],
    };

    public static ReconcileResult Check(ParsedReceipt r)
    {
        var result = new ReconcileResult();
        var lineFixes = new Dictionary<int, decimal>();

        foreach (var (line, text) in r.Unparsed)
            result.Issues.Add(new(Severity.Block, "UNPARSED", $"Couldn't read this line: \"{text}\"", line));

        foreach (var item in r.Items) CheckItem(item, result, lineFixes);
        foreach (var g in r.Groups) CheckGroup(r, g, result, lineFixes);

        decimal sum = r.Items.Where(i => i.GroupId is null).Sum(i => (i.LineTotal ?? 0m) + i.Discount + i.Fees)
                      + r.Items.Where(i => i.GroupId is not null).Sum(i => i.Fees)
                      + r.Groups.Sum(g => g.Total);
        result.CalculatedSubtotal = sum;
        result.CalculatedItemCount = (int)r.Items.Where(i => !i.IsNonSale)
            .Sum(i => i.Unit == PriceUnit.Kg ? 1m : i.Quantity);

        if (r.Subtotal is not { } subtotal)
            result.Issues.Add(new(Severity.Block, "NO_SUBTOTAL", r.Total is null
                ? "The bottom of this receipt seems to be missing, so nothing can be checked against its totals. Rescan the whole receipt if you can."
                : "The printed subtotal couldn't be read."));
        else if (sum != subtotal)
        {
            result.Issues.Add(new(Severity.Block, "SUBTOTAL_GAP",
                $"Lines add up to {sum:0.00}, but the receipt's subtotal is {subtotal:0.00} (difference {subtotal - sum:0.00})."));
            // Assume the qty x price corrections are right, then search for what closes the remainder.
            var remaining = subtotal - sum - lineFixes.Sum(kv => kv.Value);
            if (remaining == 0m)
                foreach (var (line, delta) in lineFixes)
                {
                    var from = r.Items.First(i => i.SourceLine == line).LineTotal!.Value;
                    result.GapFixes.Add([new AmountFix(line, "LineTotal", from, from + delta)]);
                }
            else result.GapFixes.AddRange(FindGapFixes(r, remaining));
        }

        // The count prints low on the receipt and is often trimmed off a scan. A matching subtotal is the stronger check.
        if (r.ItemCount is not { } count)
            result.Issues.Add(new(r.Subtotal == sum ? Severity.Warning : Severity.Block, "NO_COUNT", "The printed item count couldn't be read."));
        else if (count != result.CalculatedItemCount)
            result.Issues.Add(new(Severity.Block, "COUNT_GAP",
                $"Found {result.CalculatedItemCount} items, but the receipt says {count}."));

        if (r.Subtotal is { } st && r.Tax is { } tax && r.Total is { } total && st + tax != total)
            result.Issues.Add(new(Severity.Block, "TOTAL_GAP", $"Subtotal {st:0.00} + tax {tax:0.00} doesn't equal total {total:0.00}."));
        if (r.Total is null)
            result.Issues.Add(new(Severity.Block, "NO_TOTAL", "The printed total couldn't be read."));

        NoteTiers(r, result);
        CheckTax(r, result);
        return result;
    }

    private static void CheckItem(ReceiptItem i, ReconcileResult result, Dictionary<int, decimal> fixes)
    {
        if (i.CodeKind == CodeKind.Unknown)
            result.Issues.Add(new(Severity.Check, "BAD_CODE", $"Item code \"{i.RawCode}\" isn't a recognised format.", i.SourceLine));

        if (i.GroupId is not null) return;
        if (i.UnitPrice is null || i.LineTotal is null)
        {
            result.Issues.Add(new(Severity.Block, "NO_PRICE", $"No price was found for \"{i.RawText}\".", i.SourceLine));
            return;
        }
        var expected = Math.Round(i.Quantity * i.UnitPrice.Value, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(expected - i.LineTotal.Value) > 0.01m)
        {
            // If the printed total divides evenly by the unit price, the misread is the quantity, not the total.
            if (i.Unit == PriceUnit.Each && i.UnitPrice > 0 && i.LineTotal.Value % i.UnitPrice.Value == 0m)
            {
                var qty = i.LineTotal.Value / i.UnitPrice.Value;
                result.Issues.Add(new(Severity.Check, "QTY_MATH",
                    $"Quantity reads as {i.Quantity}, but {i.LineTotal:0.00} / {i.UnitPrice:0.00} = {qty:0}.",
                    i.SourceLine, $"Quantity {qty:0}", qty));
                return;
            }
            result.Issues.Add(new(Severity.Check, "LINE_MATH",
                $"{i.Quantity} x {i.UnitPrice:0.00} = {expected:0.00}, but the line shows {i.LineTotal:0.00}.",
                i.SourceLine, $"{expected:0.00}", expected));
            fixes[i.SourceLine] = expected - i.LineTotal.Value;
        }
    }

    private static void CheckGroup(ParsedReceipt r, MultiBuyGroup g, ReconcileResult result, Dictionary<int, decimal> fixes)
    {
        var memberQty = r.Items.Where(i => i.GroupId == g.Id).Sum(i => i.Quantity);
        if (memberQty != g.Quantity)
            result.Issues.Add(new(Severity.Check, "GROUP_QTY",
                $"Multi-buy lines add up to {memberQty} items, but the receipt shows {g.Quantity}.", g.SourceLine));
        if (g.DealCount > 0)
        {
            var expected = (g.Quantity - g.ExtraQuantity) * g.DealPrice / g.DealCount + g.ExtraTotal;
            if (Math.Abs(expected - g.Total) > 0.01m)
                result.Issues.Add(new(Severity.Warning, "GROUP_MATH",
                    $"{g.Quantity} at {g.DealCount}/{g.DealPrice:0.00} = {expected:0.00}, but the receipt shows {g.Total:0.00}.", g.SourceLine));
        }
    }

    private static void NoteTiers(ParsedReceipt r, ReconcileResult result)
    {
        foreach (var g in r.Groups.Where(g => g.ExtraQuantity > 0))
        {
            var names = string.Join(", ", r.Items.Where(i => i.GroupId == g.Id).Select(i => i.Name));
            result.Issues.Add(new(Severity.Warning, "MIXED_PRICE",
                $"{names}: some units were at a lower price and {g.ExtraQuantity:0} at a higher price. The higher price is used as the cost for all of them."));
        }
    }

    private static void CheckTax(ParsedReceipt r, ReconcileResult result)
    {
        if (r.Tax is null || r.TaxBase is null || r.TaxRatePercent is null)
        {
            result.Issues.Add(new(Severity.Warning, "NO_TAX", "The tax line couldn't be read, so tax wasn't checked."));
            return;
        }
        var tax = Math.Round(r.TaxBase.Value * r.TaxRatePercent.Value / 100m, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(tax - r.Tax.Value) > 0.01m)
            result.Issues.Add(new(Severity.Warning, "TAX_MATH", $"Tax should be {tax:0.00}, but the receipt shows {r.Tax:0.00}."));

        var taxable = r.Items.Where(i => i.IsTaxable && i.GroupId is null).Sum(i => (i.LineTotal ?? 0m) + i.Discount)
                      + r.Groups.Where(g => r.Items.Any(i => i.GroupId == g.Id && i.IsTaxable)).Sum(g => g.Total);
        if (Math.Abs(taxable - r.TaxBase.Value) > 0.01m)
            result.Issues.Add(new(Severity.Warning, "TAX_BASE", $"Taxable lines total {taxable:0.00}, but the receipt's tax base is {r.TaxBase:0.00}."));
    }

    /// <summary>Single, then paired, look-alike digit changes that close the gap exactly.</summary>
    private static IEnumerable<IReadOnlyList<AmountFix>> FindGapFixes(ParsedReceipt r, decimal gap)
    {
        if (gap == 0m) yield break;
        var candidates = new List<AmountFix>();
        foreach (var i in r.Items.Where(x => x.GroupId is null && x.LineTotal is not null).OrderBy(x => x.Confidence))
            foreach (var to in Variants(i.LineTotal!.Value))
                candidates.Add(new(i.SourceLine, "LineTotal", i.LineTotal!.Value, to));
        foreach (var g in r.Groups)
            foreach (var to in Variants(g.Total))
                candidates.Add(new(g.SourceLine, "Total", g.Total, to));

        int found = 0;
        foreach (var c in candidates.Where(c => c.To - c.From == gap))
        {
            yield return [c];
            if (++found >= 5) yield break;
        }
        if (found > 0) yield break;
        for (int a = 0; a < candidates.Count; a++)
            for (int b = a + 1; b < candidates.Count; b++)
                if (candidates[a].SourceLine != candidates[b].SourceLine
                    && candidates[a].To - candidates[a].From + candidates[b].To - candidates[b].From == gap)
                {
                    yield return [candidates[a], candidates[b]];
                    if (++found >= 5) yield break;
                }
    }

    private static IEnumerable<decimal> Variants(decimal amount)
    {
        var s = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        for (int p = 0; p < s.Length; p++)
            if (LookAlikes.TryGetValue(s[p], out var alts))
                foreach (var alt in alts)
                    yield return decimal.Parse(s[..p] + alt + s[(p + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }
}
