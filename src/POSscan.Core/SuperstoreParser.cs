using System.Globalization;
using System.Text.RegularExpressions;

namespace POSscan.Core;

/// <summary>
/// Template for Real Canadian Superstore receipts. Patterns tolerate the character confusions seen on
/// thermal scans (for example "@" read as 8, 0, 9 or a). A line that fits no pattern is kept in
/// <see cref="ParsedReceipt.Unparsed"/>, never dropped.
/// </summary>
public static partial class SuperstoreParser
{
    const string Amt = @"-?\d{1,4}[.,]\d{2}";

    [GeneratedRegex(@"^\d{1,2}\s*[-.]*\s*[A-Za-z][A-Za-z ]{2,}$")] private static partial Regex Dept();
    [GeneratedRegex(@"^SUB\s*T", RegexOptions.IgnoreCase)] private static partial Regex SubtotalLine();
    [GeneratedRegex(@"^t[o0]ta[l1]\b", RegexOptions.IgnoreCase)] private static partial Regex TotalLine();
    // "RECYCLING FEE" or "DEPOSIT 1", then whatever follows on the row: its rate and amount, or text showing through from the back.
    [GeneratedRegex(@"^(?:re?c[a-z0-9 ]{0,12}?\s*f[e3]{1,2}\S*|dep[o0]s[a-z0-9.]*(?:\s+\d\b)?)(.*)$", RegexOptions.IgnoreCase)] private static partial Regex FeeLabel();
    [GeneratedRegex(@"^\d{1,3}[.,]\d\d$")] private static partial Regex FeeAmountToken();
    // "4 @ $2.94 <any amount of show-through> 11.76", trusted only when the numbers multiply out.
    [GeneratedRegex(@"^(\d{1,3})\s+\S{1,2}\s+[$sS]?(\d+[.,]\d\d)(?:\s+[e8a][a8])?\s+.*?(\d+[.,]\d\d)$")] private static partial Regex NoisyQuantityPrice();
    [GeneratedRegex(@"^\$?(\d+[.,]\d\d)\s+e.*?\d/\$?\d+[.,]\d\d")] private static partial Regex MultiBuyInfo();
    [GeneratedRegex(@"^(\d{1,3})\s+\S{1,2}\s+(\d)/[$3Ss]?(\d+[.,]\d\d)\s+(\d{1,4}[.,]\d\d)$")] private static partial Regex MultiBuyTotal();
    [GeneratedRegex(@"^(\d+[.,]\d{3})\s*k\S*\s+(?:\S+\s+){0,3}?\$?(\d+[.,]\d\d)\s*/\s*k\S*\s+(\d{1,4}[.,]\d\d)$")] private static partial Regex Weighed();
    // Stray words before the amount are text showing through from the back of the paper. The totals check covers a wrong read.
    [GeneratedRegex(@"^(\d{1,3})\s+(?:\S{1,2}\s+)?[$sS]?(\d+[.,]\d\d)(\s+[e8a][a8])?(?:(?:\s+\S+){0,3}?\s+(\d{1,4}[.,]\d\d))?$")] private static partial Regex QuantityPrice();
    // "$3.79 lmt 3, $4.49 ea": a sale price with a limit, then the regular price. "l" is often read as 1 or i, and "m" as n.
    [GeneratedRegex(@"^\$?\d+[.,]\d\d\s+[l1iI][mn]t\s*\d+\s*[.,]?\s*\$?(\d+[.,]\d\d)\s*[e8][a8]")] private static partial Regex LimitInfo();
    [GeneratedRegex(@"^[G6B8SU]?P?M[RHA.]{0,2}[JS]\s+(-?\d{1,4}[.,]\d\d)$")] private static partial Regex WrappedFlags();
    [GeneratedRegex(@"^\S{1,3}\$?\d[.,]\d\d$")] private static partial Regex FeeRate();
    [GeneratedRegex(@"PERSTORE|EAL\s*CANAD|^RCSS\b", RegexOptions.IgnoreCase)] private static partial Regex Header();
    [GeneratedRegex(@"(?:s.t\s*pr|pr[il1]c).*?(-\d+[.,]\d\d)\s*$", RegexOptions.IgnoreCase)] private static partial Regex DiscountLine();
    [GeneratedRegex(@"^(\S*?)(\d{1,2})[)\]](\S+)$")] private static partial Regex PrefixedCode();
    [GeneratedRegex(@"^[1(|lI](\d)[1)|lI\]](\d{11})$")] private static partial Regex MangledPrefix();
    [GeneratedRegex(@"^[G6B8SU]?P?[MHNAWK#][RHA.]{1,2}[JS]$|^G?PMJ$|^[RHN][QU0!]$")] private static partial Regex Flags();
    [GeneratedRegex(@"(\d)[.,] (\d\d)(?=\s|$)")] private static partial Regex SplitDecimal();
    [GeneratedRegex(@"^(?:\.{2,}\s+)+")] private static partial Regex LeadingNoise();
    [GeneratedRegex(@"\b(99\d{20,26})\b")] private static partial Regex TransactionCode();
    [GeneratedRegex(@"\b(20\d\d)/(\d\d)/(\d\d)")] private static partial Regex FooterDate();

    [GeneratedRegex(@"^(\S{0,2}\d{1,2}[)\]])\s+")] private static partial Regex SpacedPrefix();

    // After a fee label: its amount is still to come (on one of the next two rows), or only its "4@$0.02" rate is.
    private enum Pending { None, FeeAmount, FeeRate }

    public static bool Matches(IEnumerable<OcrLine> lines) =>
        lines.Take(12).Any(l => Regex.IsMatch(l.Text, @"SUP.?R.?ST|REAL\s+CANA", RegexOptions.IgnoreCase) || Header().IsMatch(l.Text));

    /// <summary>The store logo or store-number line that starts a receipt.</summary>
    public static bool IsHeader(string text) => Header().IsMatch(text.Trim());

    public static bool IsSubtotal(string text) => SubtotalLine().IsMatch(Clean(text));

    /// <summary>A department heading such as "22-DAIRY", which sits between items and belongs to none of them.</summary>
    public static bool IsDepartment(string text) => Dept().IsMatch(Clean(text));

    /// <summary>A department heading or item line: what follows a header on a real receipt, and not on a footer that names the store.</summary>
    public static bool StartsItems(string text)
    {
        var t = Clean(text);
        return t.Length > 0 && (Dept().IsMatch(t) || LooksLikeCode(t.Split(' ')[0]));
    }

    public static ParsedReceipt Parse(IReadOnlyList<OcrLine> lines)
    {
        var r = new ParsedReceipt();
        foreach (var l in lines)
        {
            r.TransactionId ??= TransactionCode().Match(l.Text) is { Success: true } t ? t.Groups[1].Value : null;
            if (r.Date is null && FooterDate().Match(l.Text) is { Success: true } d
                && DateOnly.TryParseExact($"{d.Groups[1]}-{d.Groups[2]}-{d.Groups[3]}", "yyyy-MM-dd", out var date))
                r.Date = date;
        }

        // Everything before the first department heading (card slip, address) is ignored by design.
        int start = -1;
        for (int i = 0; i + 1 < lines.Count && start < 0; i++)
            if (Dept().IsMatch(Clean(lines[i].Text))
                && Clean(lines[i + 1].Text).Split(' ')[0] is var first && LooksLikeCode(first))
                start = i;
        if (start < 0) return r;

        string dept = "";
        ReceiptItem? current = null;
        var openGroup = new List<ReceiptItem>();
        decimal? groupRegular = null;
        var pending = Pending.None;
        int feeRows = 0;
        MultiBuyGroup? lastGroup = null;
        ReceiptItem? quantityRead = null; // The item whose quantity line has been read, so a second one is a second price.

        int n = start;
        for (; n < lines.Count; n++)
        {
            var t = Clean(lines[n].Text);
            if (t.Length == 0) continue;
            if (SubtotalLine().IsMatch(t)) break;

            // Rows under a fee label belong to that fee until the next item, department or fee starts.
            if (pending != Pending.None && current is not null && !StartsBlock(t))
            {
                var (rate, amount) = FeeParts(t);
                if (pending == Pending.FeeAmount && amount is { } fee)
                {
                    current.Fees += fee;
                    pending = rate ? Pending.None : Pending.FeeRate;
                    continue;
                }
                if (rate && amount is null && feeRows++ < 2)
                {
                    if (pending == Pending.FeeRate) pending = Pending.None;
                    continue;
                }
            }
            pending = Pending.None;

            if (Dept().IsMatch(t)) { dept = Regex.Replace(t, @"^\d+\s*-?\s*", ""); current = null; continue; }
            if (current is not null && FeeLabel().Match(t) is { Success: true } label)
            {
                var (rate, amount) = FeeParts(label.Groups[1].Value.Trim());
                if (amount is { } fee) current.Fees += fee;
                pending = amount is null ? Pending.FeeAmount : rate ? Pending.None : Pending.FeeRate;
                feeRows = 0;
                continue;
            }

            var m = MultiBuyInfo().Match(t);
            if (m.Success) { groupRegular = Dec(m.Groups[1].Value); continue; }

            m = LimitInfo().Match(t);
            if (m.Success) { groupRegular = Dec(m.Groups[1].Value); continue; }

            // Tax flags that wrapped onto their own row, with the amount already read on the item's row.
            if (current is not null && Flags().IsMatch(t)) { current.TaxFlags = t; continue; }

            m = WrappedFlags().Match(t);
            if (m.Success && current is { UnitPrice: null })
            {
                var flags = t.Split(' ')[0];
                current.TaxFlags = flags;
                current.UnitPrice = current.LineTotal = Dec(m.Groups[1].Value);
                openGroup.Remove(current);
                continue;
            }

            m = MultiBuyTotal().Match(t);
            if (m.Success && openGroup.Count > 0)
            {
                var g = new MultiBuyGroup
                {
                    Id = r.Groups.Count + 1, SourceLine = n, Quantity = Dec(m.Groups[1].Value),
                    DealCount = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), DealPrice = Dec(m.Groups[3].Value),
                    Total = Dec(m.Groups[4].Value), RegularPrice = groupRegular,
                };
                CloseGroup(g);
                continue;
            }

            m = Weighed().Match(t);
            if (m.Success && current is not null)
            {
                current.Unit = PriceUnit.Kg;
                current.Quantity = Dec(m.Groups[1].Value);
                current.UnitPrice = Dec(m.Groups[2].Value);
                current.LineTotal = Dec(m.Groups[3].Value);
                if (StrayDollarPrice(current.UnitPrice.Value, current.Quantity, current.LineTotal.Value, tolerance: 0.01m) is { } perKg)
                    current.UnitPrice = perKg;
                openGroup.Remove(current);
                continue;
            }

            if (!QuantityPrice().IsMatch(t) && CheckedQuantityLine(t) is { } tidy) t = tidy;
            m = QuantityPrice().Match(t);
            // "3 @ $3.29 ea" under several unpriced lines is one sale price shared across them.
            if (m.Success && m.Groups[3].Success && m.Groups[4].Success && openGroup.Count > 1)
            {
                CloseGroup(new MultiBuyGroup
                {
                    Id = r.Groups.Count + 1, SourceLine = n, Quantity = Dec(m.Groups[1].Value), DealCount = 1,
                    DealPrice = Dec(m.Groups[2].Value), Total = Dec(m.Groups[4].Value), RegularPrice = groupRegular,
                });
                continue;
            }
            // "2 @ $1.49 ea" straight after a deal is the units past its limit.
            if (m.Success && m.Groups[4].Success && current is null && lastGroup is not null)
            {
                lastGroup.Quantity += Dec(m.Groups[1].Value);
                lastGroup.Total += Dec(m.Groups[4].Value);
                lastGroup.ExtraQuantity += Dec(m.Groups[1].Value);
                lastGroup.ExtraTotal += Dec(m.Groups[4].Value);
                continue;
            }
            // A second quantity line on one item: "3 @ $3.79 ea" then "1 @ $4.49 ea". Two prices, so it's costed as a group.
            if (m.Success && m.Groups[4].Success && current is not null && current == quantityRead && current.LineTotal is { } firstTotal)
            {
                var extraQty = Dec(m.Groups[1].Value);
                var extraTotal = Dec(m.Groups[4].Value);
                var g = new MultiBuyGroup
                {
                    Id = r.Groups.Count + 1, SourceLine = n, Quantity = current.Quantity + extraQty, DealCount = 1,
                    DealPrice = current.UnitPrice ?? 0m, Total = firstTotal + extraTotal, RegularPrice = groupRegular,
                    ExtraQuantity = extraQty, ExtraTotal = extraTotal,
                };
                current.Quantity = g.Quantity;
                openGroup.Clear();
                openGroup.Add(current);
                CloseGroup(g);
                continue;
            }
            if (m.Success && current is not null)
            {
                var printedQty = Dec(m.Groups[1].Value);
                current.UnitPrice = Dec(m.Groups[2].Value);
                if (m.Groups[4].Success) current.LineTotal = Dec(m.Groups[4].Value);
                quantityRead = current;
                // The quantity prints twice. If they disagree, keep the one that agrees with price x total.
                bool prefixFits = current.HasPrefixQuantity && current.Quantity * current.UnitPrice == current.LineTotal;
                var prefixQty = current.HasPrefixQuantity ? current.Quantity : printedQty;
                if (!prefixFits) current.Quantity = printedQty;
                if (current.LineTotal is { } lineTotal && current.Quantity * current.UnitPrice != lineTotal)
                {
                    // Either printed quantity may be the one that makes the corrected price fit.
                    foreach (var qty in new[] { current.Quantity, prefixQty }.Distinct())
                        if (StrayDollarPrice(current.UnitPrice.Value, qty, lineTotal, tolerance: 0.01m) is { } each)
                        {
                            current.Quantity = qty;
                            current.UnitPrice = each;
                            break;
                        }
                }
                openGroup.Remove(current);
                continue;
            }

            m = DiscountLine().Match(t);
            if (m.Success && current is not null) { current.Discount += Dec(m.Groups[1].Value); continue; }

            var item = TryParseItem(t, n, lines[n].Confidence, dept);
            if (item is not null)
            {
                r.Items.Add(item);
                current = item;
                lastGroup = null;
                // An item with no price yet is a multi-buy member or awaits a quantity line.
                if (item.UnitPrice is null) openGroup.Add(item);
                continue;
            }
            r.Unparsed.Add((n, lines[n].Text));
        }

        ParseFooter(lines, n, r);
        return r;

        void CloseGroup(MultiBuyGroup g)
        {
            r.Groups.Add(g);
            foreach (var member in openGroup) member.GroupId = g.Id;
            openGroup.Clear();
            groupRegular = null;
            current = null;
            lastGroup = g;
        }
    }

    /// <summary>
    /// A quantity row with too much show-through for <see cref="QuantityPrice"/>, rewritten as a plain one. The last amount
    /// can have junk stuck to its front ("bo11.00"), so each length is tried, and it's kept only where quantity x price
    /// equals it exactly.
    /// </summary>
    private static string? CheckedQuantityLine(string t)
    {
        var m = NoisyQuantityPrice().Match(t);
        if (!m.Success) return null;
        var qty = Dec(m.Groups[1].Value);
        var price = Dec(m.Groups[2].Value);
        var digits = m.Groups[3].Value;
        for (int start = 0; start < digits.Length - 3; start++)
        {
            var total = Dec(digits[start..]);
            if (qty > 0m && qty * price == total)
                return $"{m.Groups[1].Value} @ ${m.Groups[2].Value} {digits[start..]}";
        }
        return null;
    }

    /// <summary>A row that starts something new, so it can't be the rest of a fee above it.</summary>
    private static bool StartsBlock(string t) =>
        Dept().IsMatch(t) || SubtotalLine().IsMatch(t) || FeeLabel().IsMatch(t) || LooksLikeCode(t.Split(' ')[0]);

    /// <summary>
    /// Reads what follows a fee label: whether a "4@$0.02" rate is there, and the fee amount, which is the last thing on
    /// the row. A rate on its own, like "4@$0.10", is not an amount. Words in between are text showing through the paper.
    /// </summary>
    private static (bool Rate, decimal? Amount) FeeParts(string rest)
    {
        var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return (false, null);
        bool rate = FeeRate().IsMatch(tokens[0]) || Regex.IsMatch(rest, @"^\d{1,2}\s*[@0aG]");
        if (tokens.Length == 1 && FeeRate().IsMatch(tokens[0])) return (true, null);
        return FeeAmountToken().IsMatch(tokens[^1]) ? (rate, Dec(tokens[^1])) : (rate, null);
    }

    /// <summary>
    /// A "$" can read as a digit, so "3 @ $5.00 15.00" arrives as "3 @ 85.00 15.00". The price without its first digit
    /// is used only when it then agrees with the quantity and line total. Any other disagreement is left as printed for
    /// the totals checks to flag, because either number could be the misread one: "3 @ $3.50 10.50" with a faded 1
    /// reads as "3 @ $3.50 0.50", and working the price out from that total would quietly give 0.17.
    /// </summary>
    /// <param name="tolerance">A cent allows for deals like 3 for $10.00, printed as "3 @ $3.33".</param>
    private static decimal? StrayDollarPrice(decimal price, decimal qty, decimal total, decimal tolerance)
    {
        if (qty <= 0m || total <= 0m) return null;
        var text = price.ToString("0.00", CultureInfo.InvariantCulture);
        if (text.Length < 5) return null;
        var rest = Dec(text[1..]);
        return Math.Abs(Math.Round(rest * qty, 2, MidpointRounding.AwayFromZero) - total) <= tolerance ? rest : null;
    }

    private static ReceiptItem? TryParseItem(string t, int lineNo, double conf, string dept)
    {
        var tokens = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count < 2) return tokens.Count == 1 && LooksLikeCode(tokens[0]) ? NewItem(tokens[0]) : null;
        if (!LooksLikeCode(tokens[0])) return null;
        var item = NewItem(tokens[0]);
        tokens.RemoveAt(0);

        // A faded decimal point: "MRJ 5 50" is 5.50, but only straight after the tax flags, where nothing else can be.
        if (tokens.Count >= 3 && Flags().IsMatch(tokens[^3]) && Regex.IsMatch(tokens[^2], @"^\d{1,3}$") && Regex.IsMatch(tokens[^1], @"^\d\d$"))
        {
            tokens[^2] = $"{tokens[^2]}.{tokens[^1]}";
            tokens.RemoveAt(tokens.Count - 1);
        }
        if (Regex.IsMatch(tokens[^1], $"^{Amt}$"))
        {
            item.UnitPrice = Dec(tokens[^1]);
            item.LineTotal = item.UnitPrice;
            tokens.RemoveAt(tokens.Count - 1);
        }
        if (tokens.Count > 0 && Flags().IsMatch(tokens[^1]))
        {
            item.TaxFlags = tokens[^1];
            tokens.RemoveAt(tokens.Count - 1);
        }
        item.Name = string.Join(' ', tokens);
        return item;

        ReceiptItem NewItem(string first)
        {
            var (qty, code) = SplitCode(first);
            return new ReceiptItem
            {
                SourceLine = lineNo, RawText = t, RawCode = code, CodeKind = Upc.Classify(code), Confidence = conf,
                Department = dept, Quantity = qty ?? 1m, HasPrefixQuantity = qty.HasValue,
            };
        }
    }

    private static (decimal? Qty, string Code) SplitCode(string token)
    {
        var m = PrefixedCode().Match(token);
        if (m.Success) return (Dec(m.Groups[2].Value), m.Groups[3].Value);
        m = MangledPrefix().Match(token);
        if (m.Success) return (Dec(m.Groups[1].Value), m.Groups[2].Value);
        return (null, token);
    }

    private static bool LooksLikeCode(string token)
    {
        var (_, code) = SplitCode(token);
        int digits = code.Count(char.IsAsciiDigit);
        if (digits < 3 || digits < code.Length * 0.7 || code.Contains('$')) return false;
        // A short token with a dot is a price, but a long one is a code with a misread digit.
        return !code.Contains('.') && !code.Contains(',') || code.Length >= 8;
    }

    private static void ParseFooter(IReadOnlyList<OcrLine> lines, int from, ParsedReceipt r)
    {
        for (int i = from; i < Math.Min(lines.Count, from + 8); i++)
        {
            var t = Clean(lines[i].Text);
            if (SubtotalLine().IsMatch(t)) r.Subtotal = LastAmount(t);
            else if (TotalLine().IsMatch(t)) r.Total = LastAmount(t);
            else if (Regex.IsMatch(t, @"\bof\s+ite", RegexOptions.IgnoreCase)
                     && Regex.Match(t, @"(\d+)\s*$") is { Success: true } c)
                r.ItemCount = int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (Regex.Match(t, @"(\d+[.,]\d\d)\s+\S+\s+(\d[.,]\d{3})\d*\S*\s+(\d+[.,]\d\d)$") is { Success: true } tax)
            {
                r.TaxBase = Dec(tax.Groups[1].Value);
                r.TaxRatePercent = Dec(tax.Groups[2].Value);
                r.Tax = Dec(tax.Groups[3].Value);
            }
        }
    }

    private static decimal? LastAmount(string t)
    {
        var m = Regex.Match(t, @"(\d{1,5})[.,\-](\d\d)\)?$");
        return m.Success ? Dec($"{m.Groups[1].Value}.{m.Groups[2].Value}") : null;
    }

    private static string Clean(string s)
    {
        s = Regex.Replace(s.Trim(), @"\s+", " ");
        s = LeadingNoise().Replace(s, "");
        s = SpacedPrefix().Replace(s, "$1");
        return SplitDecimal().Replace(s, "$1.$2");
    }

    private static decimal Dec(string s) => decimal.Parse(s.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture);
}
