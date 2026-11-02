namespace POSscan.Core;

public enum CodeKind { Unknown, Upc11, InStore7, ShortCode }
public enum PriceUnit { Each, Kg }
public enum Severity { Warning, Check, Block }

/// <summary>One visual line of receipt text from OCR or a text layer.</summary>
public sealed record OcrLine(string Text, double Confidence = 1.0, int Top = 0, int Bottom = 0, int Left = 0, int Right = 0);

public sealed class ReceiptItem
{
    public required int SourceLine { get; init; }
    public required string RawText { get; init; }
    public required string RawCode { get; set; }
    public CodeKind CodeKind { get; set; }
    public string Name { get; set; } = "";
    public string Department { get; set; } = "";
    public string TaxFlags { get; set; } = "";
    public double Confidence { get; set; } = 1.0;

    public decimal Quantity { get; set; } = 1m;
    public PriceUnit Unit { get; set; } = PriceUnit.Each;
    public bool HasPrefixQuantity { get; set; }

    /// <summary>Printed price per unit, before discount. Null until a price line is found.</summary>
    public decimal? UnitPrice { get; set; }
    /// <summary>Printed extended amount. Null for multi-buy members, whose total lives on the group.</summary>
    public decimal? LineTotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Fees { get; set; }
    public int? GroupId { get; set; }

    public bool IsTaxable => TaxFlags.Length >= 4 && "G6B8SU".Contains(char.ToUpperInvariant(TaxFlags[0]));
    /// <summary>Points items and coupons: nothing was bought at this line, though a coupon still counts toward the subtotal.</summary>
    public bool IsNonSale => UnitPrice <= 0m && (LineTotal ?? 0m) <= 0m;
}

public sealed class MultiBuyGroup
{
    public required int Id { get; init; }
    public required int SourceLine { get; init; }
    public decimal Quantity { get; set; }
    public int DealCount { get; init; }
    public decimal DealPrice { get; init; }
    public decimal Total { get; set; }
    public decimal? RegularPrice { get; init; }
    /// <summary>Units past the deal limit, charged on their own line ("2 @ $1.49 ea"). Included in Quantity and Total.</summary>
    public decimal ExtraQuantity { get; set; }
    public decimal ExtraTotal { get; set; }
}

public sealed class ParsedReceipt
{
    public string Supplier { get; init; } = "Real Canadian Superstore";
    public string? TransactionId { get; set; }
    public DateOnly? Date { get; set; }
    public List<ReceiptItem> Items { get; } = [];
    public List<MultiBuyGroup> Groups { get; } = [];
    public List<(int Line, string Text)> Unparsed { get; } = [];
    public decimal? Subtotal { get; set; }
    public decimal? TaxBase { get; set; }
    public decimal? TaxRatePercent { get; set; }
    public decimal? Tax { get; set; }
    public decimal? Total { get; set; }
    public int? ItemCount { get; set; }

    /// <summary>
    /// The cost of one unit, excluding fees. A sale price counts as the cost, printed discounts and coupons are ignored,
    /// and when units were bought at more than one price the highest applies to all of them. Null if not yet known.
    /// </summary>
    public decimal? ActualUnitCost(ReceiptItem item)
    {
        if (item.GroupId is { } id)
        {
            var g = Groups.First(x => x.Id == id);
            if (g.Quantity <= 0) return null;
            var dealQuantity = g.Quantity - g.ExtraQuantity;
            if (g.ExtraQuantity <= 0 || dealQuantity <= 0) return Round(g.Total / g.Quantity);
            return Math.Max(Round((g.Total - g.ExtraTotal) / dealQuantity), Round(g.ExtraTotal / g.ExtraQuantity));
        }
        if (item.UnitPrice is not { } price) return null;
        return price;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

public sealed record ReconcileIssue(
    Severity Severity, string Code, string Message, int? SourceLine = null, string? Suggestion = null, decimal? SuggestedValue = null);

public sealed record AmountFix(int SourceLine, string Field, decimal From, decimal To);

public sealed class ReconcileResult
{
    public List<ReconcileIssue> Issues { get; } = [];
    public List<IReadOnlyList<AmountFix>> GapFixes { get; } = [];
    public decimal? CalculatedSubtotal { get; set; }
    public int CalculatedItemCount { get; set; }
    public bool CanExport => Issues.All(i => i.Severity != Severity.Block);
}
