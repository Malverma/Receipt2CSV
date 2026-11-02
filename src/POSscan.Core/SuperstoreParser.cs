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
    [GeneratedRegex(@"^re?c.*\bf[e3].?$", RegexOptions.IgnoreCase)] private static partial Regex Recycling();
    [GeneratedRegex(@"^dep[o0]s[a-z0-9]+\s*\d?$", RegexOptions.IgnoreCase)] private static partial Regex Deposit();
    [GeneratedRegex(@"^\S*\d[.,]\d\d\s+(\d{1,3}[.,]\d\d)$")] private static partial Regex FeeAmount();
    [GeneratedRegex(@"^\$?(\d+[.,]\d\d)\s+e.*?\d/\$?\d+[.,]\d\d")] private static partial Regex MultiBuyInfo();
    [GeneratedRegex(@"^(\d{1,3})\s+\S{1,2}\s+(\d)/[$3Ss]?(\d+[.,]\d\d)\s+(\d{1,4}[.,]\d\d)$")] private static partial Regex MultiBuyTotal();
    [GeneratedRegex(@"^(\d+[.,]\d{3})\s*k\S*\s+(?:\S+\s+){0,3}?\$?(\d+[.,]\d\d)\s*/\s*k\S*\s+(\d{1,4}[.,]\d\d)$")] private static partial Regex Weighed();
    // Stray words before the amount are text showing through from the back of the paper. The totals check covers a wrong read.
    [GeneratedRegex(@"^(\d{1,3})\s+(?:\S{1,2}\s+)?[$sS]?(\d+[.,]\d\d)(\s+[e8a][a8])?(?:(?:\s+\S+){0,3}?\s+(\d{1,4}[.,]\d\d))?$")] private static partial Regex QuantityPrice();
    // "$3.79 lmt 3, $4.49 ea": a sale price with a limit, then the regular price. "l" is often read as 1 or i, and "m" as n.
    [GeneratedRegex(@"^\$?\d+[.,]\d\d\s+[l1iI][mn]t\s*\d+\s*[.,]?\s*\$?(\d+[.,]\d\d)\s*[e8][a8]")] private static partial Regex LimitInfo();
    [GeneratedRegex(@"^[G6B8SU]?P?M[RHA.]{0,2}[JS]\s+(-?\d{1,4}[.,]\d\d)$")] private static partial Regex WrappedFlags();
    // A fee with its amount on the same row, once with the "4@$0.02" rate between them and once with the rate on the next row.
    [GeneratedRegex(@"^(?:re?c\S*\s+f\S+|dep[o0]s\S*(?:\s+\d)?)\s+(?:(\S*\d[.,]\d\d)\s+)?(\d{1,3}[.,]\d\d)$", RegexOptions.IgnoreCase)] private static partial Regex InlineFee();
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

    private enum Pending { None, Fee, FeeRate }

    public static bool Matches(IEnumerable<OcrLine> lines) =>
        lines.Take(12).Any(l => Regex.IsMatch(l.Text, @"SUP.?R.?ST|REAL\s+CANA", RegexOptions.IgnoreCase) || Header().IsMatch(l.Text));

    /// <summary>The store logo or store-number line that starts a receipt.</summary>
    public static bool IsHeader(string text) => Header().IsMatch(text.Trim());

    public static bool IsSubtotal(string text) => SubtotalLine().IsMatch(Clean(text));

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
        MultiBuyGroup? lastGroup = null;
        ReceiptItem? quantityRead = null; // The item whose quantity line has been read, so a second one is a second price.

        int n = start;
        for (; n < lines.Count; n++)
        {
            var t = Clean(lines[n].Text);
            if (t.Length == 0) continue;
            if (SubtotalLine().IsMatch(t)) break;

            if (pending == Pending.FeeRate)
            {
                pending = Pending.None;
                if (FeeRate().IsMatch(t)) continue;
            }
            if (pending == Pending.Fee)
            {
                pending = Pending.None;
                var fee = FeeAmount().Match(t);
                if (fee.Success && current is not null) { current.Fees += Dec(fee.Groups[1].Value); continue; }
            }

            if (Dept().IsMatch(t)) { dept = Regex.Replace(t, @"^\d+\s*-?\s*", ""); current = null; continue; }
            if (current is not null && (Recycling().IsMatch(t) || Deposit().IsMatch(t))) { pending = Pending.Fee; continue; }
            if (current is not null && InlineFee().Match(t) is { Success: true } inline)
            {
                current.Fees += Dec(inline.Groups[2].Value);
                if (!inline.Groups[1].Success) pending = Pending.FeeRate;
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
                openGroup.Remove(current);
                continue;
            }

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
                if (!prefixFits) current.Quantity = printedQty;
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
