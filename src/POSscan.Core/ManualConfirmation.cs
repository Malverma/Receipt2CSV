using System.Globalization;

namespace POSscan.Core;

public enum ConfirmKind { Item, GroupTotal, Fees, Discount, Subtotal, Tax, Total, ItemCount }

/// <summary>One line of the walk down the receipt. Line is the receipt line it belongs to, so it survives edits.</summary>
public sealed record ConfirmStep(ConfirmKind Kind, int Line = -1);

public sealed record ConfirmInput(IReadOnlyList<ReceiptEdit> Edits, string? Error)
{
    public static ConfirmInput Fail(string error) => new([], error);
}

/// <summary>
/// Goes down a receipt one line at a time, with everything on a line in one text box, in the order the store
/// reads it: UPC, item name, new unit cost, quantity. "06870011200  HOMO MILK  5.05 x 4".
/// Line totals aren't asked for. They follow from the quantity and price, and the subtotal check covers the result.
/// </summary>
public static class ManualConfirmation
{
    public static List<ConfirmStep> Steps(ParsedReceipt r, DraftDecisions decisions)
    {
        var steps = new List<ConfirmStep>();
        foreach (var i in r.Items.Where(i => i.UnitPrice != 0m && !decisions.Skipped.Contains(i.SourceLine)))
        {
            steps.Add(new(ConfirmKind.Item, i.SourceLine));
            if (i.Fees != 0m) steps.Add(new(ConfirmKind.Fees, i.SourceLine));
            if (i.Discount != 0m) steps.Add(new(ConfirmKind.Discount, i.SourceLine));
        }
        steps.AddRange(r.Groups.Select(g => new ConfirmStep(ConfirmKind.GroupTotal, g.SourceLine)));
        steps = steps.OrderBy(s => s.Line).ThenBy(s => s.Kind).ToList();

        steps.Add(new(ConfirmKind.Subtotal));
        if (r.Tax is not null) steps.Add(new(ConfirmKind.Tax));
        steps.Add(new(ConfirmKind.Total));
        if (r.ItemCount is not null) steps.Add(new(ConfirmKind.ItemCount));
        return steps;
    }

    public static string Title(ParsedReceipt r, ConfirmStep s) => s.Kind switch
    {
        ConfirmKind.Item => "Check this item",
        ConfirmKind.GroupTotal => "Total for the multi-buy above",
        ConfirmKind.Fees => $"Fees on {Item(r, s)?.Name}",
        ConfirmKind.Discount => $"Discount on {Item(r, s)?.Name}",
        ConfirmKind.Subtotal => "Subtotal",
        ConfirmKind.Tax => "Tax",
        ConfirmKind.Total => "Total",
        _ => "Number of items",
    };

    /// <param name="shownName">The item's saved name, if it has one. Otherwise the name on the receipt is shown.</param>
    public static string Format(ParsedReceipt r, ConfirmStep s, string? shownName = null)
    {
        if (s.Kind != ConfirmKind.Item) return s.Kind switch
        {
            ConfirmKind.GroupTotal => Money(Group(r, s)?.Total),
            ConfirmKind.Fees => Money(Item(r, s)?.Fees),
            ConfirmKind.Discount => Money(Item(r, s)?.Discount),
            ConfirmKind.Subtotal => Money(r.Subtotal),
            ConfirmKind.Tax => Money(r.Tax),
            ConfirmKind.Total => Money(r.Total),
            _ => r.ItemCount?.ToString(CultureInfo.InvariantCulture) ?? "",
        };
        if (Item(r, s) is not { } i) return "";
        var qty = i.Quantity.ToString("0.###", CultureInfo.InvariantCulture);
        var name = OneLine(shownName ?? i.Name);
        var start = name.Length > 0 ? $"{i.RawCode}  {name}" : i.RawCode;
        return i.GroupId is not null ? $"{start}  x {qty}" : $"{start}  {Money(i.UnitPrice)} x {qty}";
    }

    public static string Hint(ParsedReceipt r, ConfirmStep s)
    {
        if (s.Kind != ConfirmKind.Item) return "";
        if (Item(r, s) is not { } i) return "";
        return i.GroupId is not null ? "UPC, item name, x quantity"
            : i.Unit == PriceUnit.Kg ? "UPC, item name, new cost per kg x weight in kg" : "UPC, item name, new unit cost x quantity";
    }

    /// <summary>Warns before Enter replaces a printed line total that doesn't follow from the quantity and price.</summary>
    public static string? Warning(ParsedReceipt r, ConfirmStep s)
    {
        if (s.Kind != ConfirmKind.Item || Item(r, s) is not { GroupId: null, UnitPrice: { } price, LineTotal: { } total } i) return null;
        var expected = Round(i.Quantity * price);
        return Math.Abs(expected - total) > 0.01m
            ? $"The receipt reads a line total of {total:0.00}, but {i.Quantity:0.###} x {price:0.00} = {expected:0.00}. Enter uses {expected:0.00}. Correct the numbers if the total is the right one."
            : null;
    }

    public static ConfirmInput Read(ParsedReceipt r, ConfirmStep s, string text, string? shownName = null)
    {
        var edit = s.Kind switch
        {
            ConfirmKind.GroupTotal => EditKind.SetGroupTotal,
            ConfirmKind.Fees => EditKind.SetFees,
            ConfirmKind.Discount => EditKind.SetDiscount,
            ConfirmKind.Subtotal => EditKind.SetSubtotal,
            ConfirmKind.Tax => EditKind.SetTax,
            ConfirmKind.Total => EditKind.SetTotal,
            ConfirmKind.ItemCount => EditKind.SetItemCount,
            _ => (EditKind?)null,
        };
        if (edit is not { } kind) return ReadItem(r, s, text, shownName);

        var clean = text.Replace("$", "").Trim();
        if (clean.Length == 0)
            return Format(r, s).Length == 0 ? new([], null) : ConfirmInput.Fail("Type the number shown on the receipt.");
        if (!TryDec(clean, out var value)) return ConfirmInput.Fail("This should be a single number, such as 12.50.");
        if (kind == EditKind.SetItemCount && (value < 0 || value != Math.Floor(value))) return ConfirmInput.Fail("The number of items should be a whole number.");
        if (kind == EditKind.SetDiscount ? value > 0 : value < 0) return ConfirmInput.Fail(kind == EditKind.SetDiscount ? "A discount should be negative, such as -0.50." : "This number can't be negative.");
        return clean == Format(r, s) || value.ToString("0.00", CultureInfo.InvariantCulture) == Format(r, s)
            ? new([], null) : new([new ReceiptEdit(kind, s.Line, Str(value))], null);
    }

    private static ConfirmInput ReadItem(ParsedReceipt r, ConfirmStep s, string text, string? shownName)
    {
        if (Item(r, s) is not { } i) return new([], null);
        var parts = text.Replace("$", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool grouped = i.GroupId is not null;

        if (parts.Length == 0) return ConfirmInput.Fail("Type the numbers shown on the receipt.");
        if (Upc.Classify(parts[0]) == CodeKind.Unknown) return ConfirmInput.Fail("The UPC should be digits only. Check it against the receipt.");
        int x = Array.FindLastIndex(parts, p => p is "x" or "X");
        if (x < 0) return ConfirmInput.Fail(grouped ? "End the line with x and the quantity, such as x 4." : "End the line with the new unit cost, x, and the quantity, such as 5.05 x 4.");
        if (x != parts.Length - 2) return ConfirmInput.Fail("Only the quantity goes after the x.");
        if (!TryDec(parts[^1], out var qty) || qty <= 0) return ConfirmInput.Fail("The quantity should be a number above zero.");

        int nameEnd = x;
        decimal price = 0m;
        if (!grouped)
        {
            // Requiring the decimal point keeps a number at the end of a name from being taken as the cost.
            if (x < 2 || !parts[x - 1].Contains('.') || !TryDec(parts[x - 1], out price))
                return ConfirmInput.Fail("Type the new unit cost before the x, such as 5.05.");
            if (price < 0 && i.UnitPrice >= 0) return ConfirmInput.Fail("The new unit cost can't be negative.");
            nameEnd = x - 1;
        }
        var nameParts = parts[1..nameEnd];
        if (nameParts.Length > 0 && nameParts.All(p => TryDec(p, out _)))
            return ConfirmInput.Fail(grouped ? "This line should only have a UPC, an item name, and a quantity." : "This line should only have a UPC, an item name, a new unit cost, and a quantity.");

        var edits = new List<ReceiptEdit>();
        if (parts[0] != i.RawCode) edits.Add(new(EditKind.SetCode, i.SourceLine, parts[0]));
        var name = string.Join(' ', nameParts);
        if (name.Length > 0 && name != OneLine(shownName ?? i.Name)) edits.Add(new(EditKind.SetName, i.SourceLine, name));
        if (qty != i.Quantity) edits.Add(new(EditKind.SetQuantity, i.SourceLine, Str(qty)));
        if (grouped) return new(edits, null);

        if (price != i.UnitPrice) edits.Add(new(EditKind.SetUnitPrice, i.SourceLine, Str(price)));
        var expected = Round(qty * price);
        if (i.LineTotal is not { } total || Math.Abs(expected - total) > 0.01m)
            edits.Add(new(EditKind.SetLineTotal, i.SourceLine, Str(expected)));
        return new(edits, null);
    }

    private static string OneLine(string name) => string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static ReceiptItem? Item(ParsedReceipt r, ConfirmStep s) => r.Items.FirstOrDefault(i => i.SourceLine == s.Line);
    private static MultiBuyGroup? Group(ParsedReceipt r, ConfirmStep s) => r.Groups.FirstOrDefault(g => g.SourceLine == s.Line);
    private static string Money(decimal? d) => d?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
    private static string Str(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    private static decimal Round(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);
    private static bool TryDec(string s, out decimal v) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v);
}
