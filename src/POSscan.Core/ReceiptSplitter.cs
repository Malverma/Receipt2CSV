namespace POSscan.Core;

/// <summary>The part of a scanned page that belongs to one receipt.</summary>
public sealed record PagePart(int Page, IReadOnlyList<OcrLine> Lines);

/// <summary>
/// Works out where each receipt starts in a scan. Stores often feed a stack of receipts into one PDF, while a long
/// receipt can also run over several pages, so page breaks alone don't say which is which.
/// </summary>
public static class ReceiptSplitter
{
    private const int TopOfPage = 12;
    private const int LogoLines = 4; // The logo and store-number lines of one header all match.

    public static List<List<PagePart>> Split(IReadOnlyList<IReadOnlyList<OcrLine>> pages)
    {
        var receipts = new List<List<PagePart>>();
        for (int p = 0; p < pages.Count; p++)
        {
            var lines = pages[p];
            int from = 0;
            foreach (var cut in Cuts(lines))
            {
                if (cut > from) Add(receipts, p, lines, from, cut);
                from = cut;
                receipts.Add([]);
            }
            if (from < lines.Count || receipts.Count == 0) Add(receipts, p, lines, from, lines.Count);
        }
        receipts.RemoveAll(r => r.Count == 0);
        return receipts;
    }

    private static void Add(List<List<PagePart>> receipts, int page, IReadOnlyList<OcrLine> lines, int from, int to)
    {
        if (receipts.Count == 0) receipts.Add([]);
        receipts[^1].Add(new PagePart(page, lines.Skip(from).Take(to - from).ToList()));
    }

    // Lines where a new receipt starts. Handwriting above a header at the top of a page stays with that receipt.
    private static List<int> Cuts(IReadOnlyList<OcrLine> lines)
    {
        var cuts = new List<int>();
        bool finished = false; // A header further down only counts once the receipt above has reached its subtotal.
        int lastHeader = -LogoLines;
        for (int i = 0; i < lines.Count; i++)
        {
            if (SuperstoreParser.IsSubtotal(lines[i].Text)) finished = true;
            if (!SuperstoreParser.IsHeader(lines[i].Text) || i - lastHeader < LogoLines) continue;
            lastHeader = i;
            bool itemsFollow = lines.Skip(i + 1).Any(l => SuperstoreParser.StartsItems(l.Text));
            if (itemsFollow && (i < TopOfPage && cuts.Count == 0 || finished))
            {
                cuts.Add(cuts.Count == 0 && i < TopOfPage ? 0 : i);
                finished = false;
            }
        }
        return cuts;
    }
}
