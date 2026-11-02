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
/// Goes down a receipt one line at a time, with every number on a line in one text box: "06870011200  4 @ 5.05".
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
        ConfirmKind.Item => Item(r, s) is { Name.Length: > 0 } i ? i.Name : "(no name)",
        ConfirmKind.GroupTotal => "Total for the multi-buy above",
        ConfirmKind.Fees => $"Fees on {Item(r, s)?.Name}",
        ConfirmKind.Discount => $"Discount on {Item(r, s)?.Name}",
        ConfirmKind.Subtotal => "Subtotal",
        ConfirmKind.Tax => "Tax",
        ConfirmKind.Total => "Total",
        _ => "Number of items",
    };

    public static string Format(ParsedReceipt r, ConfirmStep s)
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
        return i.GroupId is not null ? $"{i.RawCode}  {qty}" : $"{i.RawCode}  {qty} @ {Money(i.UnitPrice)}";
    }

    public static string Hint(ParsedReceipt r, ConfirmStep s)
    {
        if (s.Kind != ConfirmKind.Item) return "";
        if (Item(r, s) is not { } i) return "";
        return i.GroupId is not null ? "Item code, then quantity"
            : i.Unit == PriceUnit.Kg ? "Item code, then weight in kg @ price per kg" : "Item code, then quantity @ price";
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

    public static ConfirmInput Read(ParsedReceipt r, ConfirmStep s, string text)
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
        if (edit is not { } kind) return ReadItem(r, s, text);

        var clean = text.Replace("$", "").Trim();
        if (clean.Length == 0)
            return Format(r, s).Length == 0 ? new([], null) : ConfirmInput.Fail("Type the number shown on the receipt.");
        if (!TryDec(clean, out var value)) return ConfirmInput.Fail("This should be a single number, such as 12.50.");
        if (kind == EditKind.SetItemCount && (value < 0 || value != Math.Floor(value))) return ConfirmInput.Fail("The number of items should be a whole number.");
        if (kind == EditKind.SetDiscount ? value > 0 : value < 0) return ConfirmInput.Fail(kind == EditKind.SetDiscount ? "A discount should be negative, such as -0.50." : "This number can't be negative.");
        return clean == Format(r, s) || value.ToString("0.00", CultureInfo.InvariantCulture) == Format(r, s)
            ? new([], null) : new([new ReceiptEdit(kind, s.Line, Str(value))], null);
    }

    private static ConfirmInput ReadItem(ParsedReceipt r, ConfirmStep s, string text)
    {
        if (Item(r, s) is not { } i) return new([], null);
        var parts = text.Replace("@", " ").Replace("$", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool grouped = i.GroupId is not null;

        if (parts.Length == 0) return ConfirmInput.Fail("Type the numbers shown on the receipt.");
        if (Upc.Classify(parts[0]) == CodeKind.Unknown) return ConfirmInput.Fail("The item code should be digits only. Check it against the receipt.");
        if (parts.Length == 1) return ConfirmInput.Fail(grouped ? "Add the quantity after the item code." : "Add the quantity and price after the item code.");
        if (!TryDec(parts[1], out var qty) || qty <= 0) return ConfirmInput.Fail("The quantity should be a number above zero.");
        if (grouped ? parts.Length > 2 : parts.Length > 3) return ConfirmInput.Fail(grouped ? "This line should only have an item code and a quantity." : "This line should only have an item code, a quantity, and a price.");

        var edits = new List<ReceiptEdit>();
        if (parts[0] != i.RawCode) edits.Add(new(EditKind.SetCode, i.SourceLine, parts[0]));
        if (qty != i.Quantity) edits.Add(new(EditKind.SetQuantity, i.SourceLine, Str(qty)));
        if (grouped) return new(edits, null);

        if (parts.Length == 2) return ConfirmInput.Fail("Type the price after the @.");
        if (!TryDec(parts[2], out var price)) return ConfirmInput.Fail("The price should be a number, such as 5.05.");
        if (price < 0 && i.UnitPrice >= 0) return ConfirmInput.Fail("The price can't be negative.");

        if (price != i.UnitPrice) edits.Add(new(EditKind.SetUnitPrice, i.SourceLine, Str(price)));
        var expected = Round(qty * price);
        if (i.LineTotal is not { } total || Math.Abs(expected - total) > 0.01m)
            edits.Add(new(EditKind.SetLineTotal, i.SourceLine, Str(expected)));
        return new(edits, null);
    }

    private static ReceiptItem? Item(ParsedReceipt r, ConfirmStep s) => r.Items.FirstOrDefault(i => i.SourceLine == s.Line);
    private static MultiBuyGroup? Group(ParsedReceipt r, ConfirmStep s) => r.Groups.FirstOrDefault(g => g.SourceLine == s.Line);
    private static string Money(decimal? d) => d?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
    private static string Str(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    private static decimal Round(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);
    private static bool TryDec(string s, out decimal v) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v);
}
