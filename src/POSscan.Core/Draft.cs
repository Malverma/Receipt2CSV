using System.Globalization;

namespace POSscan.Core;

public enum EditKind
{
    SetCode, SetQuantity, SetUnitPrice, SetLineTotal, SetGroupTotal,
    SetSubtotal, SetTax, SetTotal, SetItemCount,
    IgnoreLine, AddItem,
    Skip, Unskip, ConfirmLine, ForceExport,
    // Saved drafts store these by number, so new kinds only ever go on the end.
    SetFees, SetDiscount,
}

/// <summary>
/// One change a person made. The receipt on screen is always the OCR result with these replayed in order,
/// which gives crash recovery and an audit trail from the same record.
/// </summary>
public sealed record ReceiptEdit(EditKind Kind, int Line = -1, string Value = "", string Name = "", decimal Quantity = 1m, decimal Price = 0m);

public sealed class DraftDecisions
{
    public HashSet<int> Skipped { get; } = [];
    public HashSet<int> Confirmed { get; } = [];
    public string? ForceReason { get; set; }
}

public static class ReceiptDraft
{
    public static (ParsedReceipt Receipt, DraftDecisions Decisions) Build(IReadOnlyList<OcrLine> lines, IEnumerable<ReceiptEdit> edits)
    {
        var receipt = SuperstoreParser.Parse(lines);
        var decisions = new DraftDecisions();
        foreach (var e in edits) Apply(receipt, decisions, e);
        receipt.Items.Sort((a, b) => a.SourceLine.CompareTo(b.SourceLine));
        return (receipt, decisions);
    }

    private static void Apply(ParsedReceipt r, DraftDecisions d, ReceiptEdit e)
    {
        var item = r.Items.FirstOrDefault(i => i.SourceLine == e.Line);
        switch (e.Kind)
        {
            case EditKind.SetCode when item is not null:
                item.RawCode = e.Value.Trim();
                item.CodeKind = Upc.Classify(item.RawCode);
                d.Confirmed.Remove(e.Line);
                break;
            case EditKind.SetQuantity when item is not null:
                item.Quantity = Dec(e.Value);
                d.Confirmed.Remove(e.Line);
                break;
            case EditKind.SetUnitPrice when item is not null:
                // A single-quantity line prints one amount, which is both the price and the total.
                if (item.UnitPrice == item.LineTotal && item.Quantity == 1m) item.LineTotal = Dec(e.Value);
                item.UnitPrice = Dec(e.Value);
                d.Confirmed.Remove(e.Line);
                break;
            case EditKind.SetLineTotal when item is not null:
                if (item.UnitPrice == item.LineTotal && item.Quantity == 1m) item.UnitPrice = Dec(e.Value);
                item.LineTotal = Dec(e.Value);
                d.Confirmed.Remove(e.Line);
                break;
            case EditKind.SetFees when item is not null: item.Fees = Dec(e.Value); break;
            case EditKind.SetDiscount when item is not null: item.Discount = Dec(e.Value); break;
            case EditKind.SetGroupTotal:
                var group = r.Groups.FirstOrDefault(g => g.SourceLine == e.Line);
                if (group is not null)
                {
                    group.Total = Dec(e.Value);
                    foreach (var member in r.Items.Where(i => i.GroupId == group.Id)) d.Confirmed.Remove(member.SourceLine);
                }
                break;
            case EditKind.SetSubtotal: r.Subtotal = Dec(e.Value); break;
            case EditKind.SetTax: r.Tax = Dec(e.Value); break;
            case EditKind.SetTotal: r.Total = Dec(e.Value); break;
            case EditKind.SetItemCount: r.ItemCount = (int)Dec(e.Value); break;
            case EditKind.IgnoreLine:
                r.Unparsed.RemoveAll(u => u.Line == e.Line);
                break;
            case EditKind.AddItem:
                r.Unparsed.RemoveAll(u => u.Line == e.Line);
                r.Items.Add(new ReceiptItem
                {
                    SourceLine = e.Line, RawText = e.Name, RawCode = e.Value.Trim(), CodeKind = Upc.Classify(e.Value.Trim()),
                    Name = e.Name, Quantity = e.Quantity, UnitPrice = e.Price,
                    LineTotal = Math.Round(e.Quantity * e.Price, 2, MidpointRounding.AwayFromZero),
                });
                break;
            case EditKind.Skip: d.Skipped.Add(e.Line); break;
            case EditKind.Unskip: d.Skipped.Remove(e.Line); break;
            case EditKind.ConfirmLine: d.Confirmed.Add(e.Line); break;
            case EditKind.ForceExport: d.ForceReason = string.IsNullOrWhiteSpace(e.Value) ? null : e.Value.Trim(); break;
        }
    }

    private static decimal Dec(string s) => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture);
}
