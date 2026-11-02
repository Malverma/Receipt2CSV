using System.Globalization;

namespace POSscan.Core;

public enum LineState { Attention, Ready, ByHand, NoChange, Skipped, NotForResale }

/// <summary>A one-click correction. Nothing is applied until a person clicks it.</summary>
public sealed record SuggestedFix(string Label, ReceiptEdit Edit);

public sealed record ReviewLine
{
    public required ReceiptItem Item { get; init; }
    public required LineState State { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<SuggestedFix> Fixes { get; init; } = [];
    public ItemMapping? Mapping { get; init; }
    public decimal? Cost { get; init; }
    public decimal? OldCost { get; init; }
    public bool IsNew => Mapping is null;
    public string DisplayName => Mapping is { EstreamName.Length: > 0 } m ? m.EstreamName : Item.Name;
}

public sealed record PriceLookup(decimal? LastCost, DateOnly? LastDate, bool HasNewerReceipt);

public sealed class ReviewSettings
{
    public decimal PriceChangePercent { get; init; } = 25m;
    public bool ExportWeighed { get; init; }
}

public sealed class ReviewResult
{
    public List<ReviewLine> Lines { get; } = [];
    public List<string> ReceiptProblems { get; } = [];
    public List<string> ReceiptNotes { get; } = [];
    public List<SuggestedFix> ReceiptFixes { get; } = [];
    public List<(int Line, string Text)> Unreadable { get; } = [];
    public bool TotalsMatch { get; set; }
    public bool Forced { get; set; }

    public int AttentionCount =>
        Lines.Count(l => l.State == LineState.Attention) + Unreadable.Count + (TotalsMatch || Forced ? 0 : 1);
    public IEnumerable<ReviewLine> ToExport => Lines.Where(l => l.State == LineState.Ready);
    public bool CanExport => AttentionCount == 0 && ToExport.Any();
}

public static class Review
{
    public static ReviewResult Build(
        ParsedReceipt r, ReconcileResult check, DraftDecisions d, ItemMatcher matcher,
        Func<string, PriceLookup> prices, ReviewSettings settings)
    {
        var result = new ReviewResult { Forced = d.ForceReason is not null };
        result.Unreadable.AddRange(r.Unparsed);

        var byLine = new Dictionary<int, List<ReconcileIssue>>();
        foreach (var i in check.Issues.Where(i => i.Code != "UNPARSED"))
        {
            var targets = TargetLines(r, i);
            if (targets.Count == 0)
            {
                (i.Severity == Severity.Block ? result.ReceiptProblems : result.ReceiptNotes).Add(i.Message);
                continue;
            }
            foreach (var t in targets)
            {
                if (!byLine.TryGetValue(t, out var list)) byLine[t] = list = [];
                list.Add(i);
            }
        }

        result.TotalsMatch = !check.Issues.Any(i => i.Severity == Severity.Block && i.Code != "UNPARSED" && i.SourceLine is null);
        // Only single changes become one-click fixes. A pair is too uncertain to offer as a button.
        foreach (var fix in check.GapFixes.Where(f => f.Count == 1))
        {
            var f = fix[0];
            var kind = f.Field == "Total" ? EditKind.SetGroupTotal : EditKind.SetLineTotal;
            result.ReceiptFixes.Add(new($"Change {NameAt(r, f.SourceLine)} from {f.From:0.00} to {f.To:0.00}", new(kind, f.SourceLine, Str(f.To))));
        }

        foreach (var item in r.Items.Where(i => !i.IsNonSale))
            result.Lines.Add(BuildLine(r, item, byLine, d, matcher, prices, settings));

        UseHighestCost(result);
        return result;
    }

    private static List<int> TargetLines(ParsedReceipt r, ReconcileIssue i)
    {
        if (i.SourceLine is not { } line) return [];
        if (r.Items.Any(x => x.SourceLine == line)) return [line];
        var group = r.Groups.FirstOrDefault(g => g.SourceLine == line);
        return group is null ? [] : r.Items.Where(x => x.GroupId == group.Id).Select(x => x.SourceLine).ToList();
    }

    private static ReviewLine BuildLine(
        ParsedReceipt r, ReceiptItem item, Dictionary<int, List<ReconcileIssue>> byLine, DraftDecisions d,
        ItemMatcher matcher, Func<string, PriceLookup> prices, ReviewSettings settings)
    {
        var match = matcher.Match(item.RawCode);
        var cost = r.ActualUnitCost(item);
        var price = prices(item.RawCode);
        var line = new ReviewLine { Item = item, State = LineState.Ready, Mapping = match.Mapping, Cost = cost, OldCost = price.LastCost };

        if (d.Skipped.Contains(item.SourceLine)) return line with { State = LineState.Skipped };
        if (match.Mapping?.NotForResale == true) return line with { State = LineState.NotForResale };

        var reasons = new List<string>();
        var fixes = new List<SuggestedFix>();
        bool confirmed = d.Confirmed.Contains(item.SourceLine);

        var issues = byLine.GetValueOrDefault(item.SourceLine, []);
        foreach (var i in issues.Where(x => x.Severity != Severity.Warning))
        {
            reasons.Add(i.Message);
            if (i.SuggestedValue is { } v)
            {
                if (i.Code == "LINE_MATH") fixes.Add(new($"Use {v:0.00}", new(EditKind.SetLineTotal, item.SourceLine, Str(v))));
                if (i.Code == "QTY_MATH") fixes.Add(new($"Use quantity {v:0}", new(EditKind.SetQuantity, item.SourceLine, Str(v))));
            }
        }

        bool weighedByHand = item.Unit == PriceUnit.Kg && !settings.ExportWeighed;
        if (weighedByHand)
            return reasons.Count > 0
                ? line with { State = LineState.Attention, Reasons = reasons, Fixes = fixes }
                : line with { State = LineState.ByHand };

        if (match.Kind == MatchKind.NearMiss)
        {
            reasons.Add("This code is one digit away from an item you've already set up. It may be misread.");
            fixes.AddRange(match.NearMisses.Select(n =>
                new SuggestedFix($"Use {n.ReceiptCode} ({n.EstreamName})", new(EditKind.SetCode, item.SourceLine, n.ReceiptCode))));
        }
        else if (match.Kind == MatchKind.None && item.CodeKind != CodeKind.Unknown)
            reasons.Add("New item. Check the code against the receipt, then confirm it.");

        if (match.Mapping is { Sku.Length: 0, Upc.Length: 0 })
            reasons.Add("This item needs an Estream SKU or UPC.");
        if (cost is null) reasons.Add("No cost could be worked out for this line.");

        if (!confirmed && match.Mapping is not null)
        {
            if (price.HasNewerReceipt)
                reasons.Add("A newer receipt already has a cost for this item. Exporting this one would replace it with an older cost.");
            if (cost is { } c && price.LastCost is { } last && last != 0m
                && Math.Abs((c - last) / last * 100m) > settings.PriceChangePercent)
                reasons.Add($"Cost changed by {(c - last) / last:+0%;-0%} from {last:0.00} on {price.LastDate:MMM d}. Is that right?");
            if (d.ForceReason is not null)
                reasons.Add("The receipt's totals don't match, so every line needs checking.");
        }

        if (reasons.Count > 0) return line with { State = LineState.Attention, Reasons = reasons, Fixes = fixes };
        if (price.LastCost is { } known && known == cost) return line with { State = LineState.NoChange };
        return line;
    }

    /// <summary>Lines for one Estream item at different costs all take the highest, and the review screen says so.</summary>
    private static void UseHighestCost(ReviewResult result)
    {
        var groups = result.Lines
            .Select((line, index) => (line, index))
            .Where(x => x.line.State is LineState.Ready or LineState.NoChange && x.line.Cost is not null)
            .GroupBy(x => ExportKey(x.line));
        foreach (var group in groups)
        {
            var highest = group.Max(x => x.line.Cost!.Value);
            if (group.All(x => x.line.Cost == highest)) continue;
            foreach (var (line, index) in group)
                result.Lines[index] = line with { Cost = highest, State = line.OldCost == highest ? LineState.NoChange : LineState.Ready };
            result.ReceiptNotes.Add($"{group.First().line.DisplayName} is on this receipt at different costs. Using the highest, {highest:0.00}.");
        }
    }

    public static string ExportKey(ReviewLine l) =>
        l.Mapping is { Sku.Length: > 0 } m ? "S" + m.Sku : "U" + (l.Mapping?.Upc ?? l.Item.RawCode);

    /// <summary>The rows for the CSV. A line that repeats an item at the same cost becomes a single row.</summary>
    public static List<BatchRow> ToBatch(ReviewResult review, string supplier)
    {
        if (!review.CanExport) throw new InvalidOperationException("This receipt still has lines to check.");
        return review.ToExport
            .GroupBy(ExportKey).Select(g => g.First())
            .Select(l => new BatchRow(
                l.Mapping!.ListingCode, l.Mapping.Sku, l.Mapping.Upc, l.Mapping.EstreamName, l.Mapping.Container,
                l.Cost!.Value, l.OldCost ?? l.Cost.Value, supplier))
            .ToList();
    }

    /// <summary>A starting point for the new-item form. Names on receipts are cut off, so a person edits this.</summary>
    public static ItemMapping NewMappingDraft(ReceiptItem item) => new(
        item.RawCode, "", item.CodeKind == CodeKind.Upc11 ? Upc.ReceiptCodeToRasUpc(item.RawCode) : "", item.Name);

    private static string NameAt(ParsedReceipt r, int line) =>
        r.Items.FirstOrDefault(i => i.SourceLine == line)?.Name ?? "the multi-buy total";

    private static string Str(decimal v) => v.ToString(CultureInfo.InvariantCulture);
}
