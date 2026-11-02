using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace POSscan.Core;

public sealed record BatchRow(
    string ListingCode, string Sku, string Upc, string Name, string Container,
    decimal NewPrice, decimal OldPrice, string Category = "")
{
    public decimal Diff => NewPrice - OldPrice;
}

public sealed record RasItem(
    int FileLine, string Section, string Category, string ListingCode, string Sku, string Upc, string Name,
    string Container, decimal NewPrice, decimal OldPrice, decimal Diff);

public sealed class RasFile
{
    public List<RasItem> Items { get; } = [];
    public Dictionary<string, int> PrintedSectionTotals { get; } = [];
    public List<string> Errors { get; } = [];
}

/// <summary>Layout settings, so a format correction doesn't need a new build.</summary>
public sealed record CsvProfile
{
    public string Name { get; init; } = "ras-wrapped";
    public int Version { get; init; } = 1;
    /// <summary>True reproduces the report text around the rows, as in the known RAS CSV.</summary>
    public bool Wrapper { get; init; } = true;
    public string ReportId { get; init; } = "RN-SO-RAS-001";
    public string Period { get; init; } = "08";
    public string StoreNumber { get; init; } = "815";
    public string StoreName { get; init; } = "THE SQUIRREL COVE GENERAL STORE";

    public static CsvProfile Load(string path) =>
        JsonSerializer.Deserialize<CsvProfile>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Empty profile: {path}");
}

public static partial class RasCsv
{
    private static readonly string[] Header1 = ["Listing", "SKU", "UPC", "Brand Name", "Container", "New", "Old", "Diff"];
    private static readonly string[] Header2 = ["Code", "", "", "", "Size", "Price", "Price", ""];
    public const string SectionIncrease = "WHOLESALE PRICE INCREASES";
    public const string SectionDecrease = "WHOLESALE PRICE DECREASES";
    public const string SectionDelist = "PENDING DE-LIST PRODUCTS";
    private static readonly string[] Sections = [SectionIncrease, SectionDecrease, SectionDelist];

    [GeneratedRegex(@"^-?\d*\.?\d+$")] private static partial Regex Numeric();

    /// <summary>
    /// Reads a RAS CSV. Reads by shape rather than column number, because in the real file price columns
    /// shift between rows: the last three values are always New, Old, Diff and the one before is the container.
    /// </summary>
    public static RasFile Read(string path) => Parse(File.ReadAllBytes(path));

    public static RasFile Parse(byte[] bytes)
    {
        var file = new RasFile();
        string section = "", category = "";
        bool inTable = false;
        int lineNo = 0;
        foreach (var row in ParseRows(Encoding.Latin1.GetString(bytes)))
        {
            lineNo++;
            var joined = string.Join(' ', row.Where(c => c.Length > 0));
            if (row.Count > 0 && row[0] == "TOTAL" && row.Count > 1)
            {
                foreach (var s in Sections)
                    if (row[1].StartsWith(s, StringComparison.Ordinal)
                        && int.TryParse(row[1][s.Length..].Trim(), CultureInfo.InvariantCulture, out var n))
                        file.PrintedSectionTotals[s] = n;
                continue;
            }
            var titled = Sections.FirstOrDefault(s => joined.StartsWith(s, StringComparison.Ordinal));
            if (titled is not null) { section = titled; inTable = true; continue; }
            if (!inTable && !TryItem(row, lineNo, section, category, out _)) continue;
            if (row.Count == 0 || row[0] is "Listing" or "Code" || joined.StartsWith("RptID", StringComparison.Ordinal)) continue;

            if (TryItem(row, lineNo, section, category, out var item)) { file.Items.Add(item!); continue; }

            // A name that wrapped onto its own row belongs to the item before it.
            if (row.Count > 3 && row[3].Length > 0 && row.Where((c, i) => i != 3).All(c => c.Length == 0) && file.Items.Count > 0)
            {
                file.Items[^1] = file.Items[^1] with { Name = file.Items[^1].Name + " " + row[3] };
                continue;
            }
            if (row[0].Length > 0 && row.Skip(1).All(c => c.Length == 0) && !row[0].All(char.IsAsciiDigit))
                category = row[0];
        }
        return Validate(file);
    }

    private static bool TryItem(List<string> row, int line, string section, string category, out RasItem? item)
    {
        item = null;
        if (row.Count < 8 || row[0] == "TOTAL") return false;
        if (row[1].Length == 0 && row[2].Length == 0) return false;
        if (!row[1].All(char.IsAsciiDigit) || !row[2].All(char.IsAsciiDigit)) return false;
        var values = row.Skip(3).Where(c => c.Length > 0).ToList();
        if (values.Count < 4) return false;
        var tail = values.TakeLast(3).ToList();
        if (!tail.All(Numeric().IsMatch)) return false;
        item = new RasItem(line, section, category, row[0], row[1], row[2],
            string.Join(' ', values.Take(values.Count - 4)), values[^4],
            D(tail[0]), D(tail[1]), D(tail[2]));
        return true;
    }

    private static RasFile Validate(RasFile f)
    {
        foreach (var i in f.Items)
        {
            if (i.NewPrice - i.OldPrice != i.Diff)
                f.Errors.Add($"Line {i.FileLine}: {i.NewPrice} - {i.OldPrice} is not {i.Diff}.");
            if (i.Sku.Length == 0 && i.Upc.Length == 0)
                f.Errors.Add($"Line {i.FileLine}: no SKU or UPC.");
        }
        foreach (var (section, printed) in f.PrintedSectionTotals)
        {
            var actual = f.Items.Count(i => i.Section == section);
            if (actual != printed) f.Errors.Add($"{section}: {actual} rows, but the file's total says {printed}.");
        }
        return f;
    }

    public static byte[] Write(IReadOnlyList<BatchRow> rows, CsvProfile profile, DateTime when, DateOnly effective)
    {
        var sb = new StringBuilder();
        void Line(params string[] cells) => sb.Append(string.Join(',', cells.Select(Quote))).Append("\r\n");
        var increases = rows.Where(r => r.Diff >= 0).ToList();
        var decreases = rows.Where(r => r.Diff < 0).ToList();

        void Table(IReadOnlyList<BatchRow> part, string section)
        {
            if (part.Count == 0) return;
            if (profile.Wrapper)
            {
                Line("", "", $"{section} for {profile.StoreName}", "", "", "", "", "");
                Line(Header1);
                Line(Header2);
            }
            foreach (var cat in part.GroupBy(r => r.Category))
            {
                if (profile.Wrapper && cat.Key.Length > 0) Line(Ascii(cat.Key), "", "", "", "", "", "", "");
                foreach (var r in cat)
                    Line(r.ListingCode, r.Sku, r.Upc, Ascii(r.Name), Container(r.Container), Num(r.NewPrice), Num(r.OldPrice), Num(r.Diff));
            }
            if (profile.Wrapper) Line("TOTAL", $"{section} {part.Count}", "", "", "", "", "", "");
        }

        if (profile.Wrapper) Preamble(sb, profile, when, effective);
        Table(increases, SectionIncrease);
        Table(decreases, SectionDecrease);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void Preamble(StringBuilder sb, CsvProfile p, DateTime when, DateOnly effective)
    {
        string date = Quote(when.ToString("MMM d, yyyy", CultureInfo.InvariantCulture));
        string time = when.ToString("h:mm:ss tt", CultureInfo.InvariantCulture);
        string eff = Quote("Effective " + effective.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture));

        // Same text and same line count as the known-good file, in case the importer skips a fixed number of lines.
        foreach (var line in new[]
        {
            $"Next RAS: {p.StoreNumber} {p.StoreName}",
            $"RptID: {p.ReportId},{date},{time}",
            $",{Quote(p.StoreName)},",
            $",RAS Wholesale Product List for BC LDB Financial Period {p.Period},",
            $",{eff},",
            "All Prices are subject to change without notice.,,",
            "\"All alcoholic beverage containers, including spirits and wines, are subject to container deposit and refund.\",,",
            "Products purchased from a private distributor may not be included since there may be a delay in receiving sales data from,,",
            "some suppliers.,,",
            "The UPC printed on products purchased from private distributors may not match the UPC printed on the report if the,,",
            "supplier does not provide the updated UPC to the BC Liquor Distribution Branch.,,",
            "This report is based on products you have ordered through the BC Liquor Distribution Branch and other authorized sources,,",
            "in the last twelve (12) months.,This report lists only the products with the following changes:,",
            "- Wholesale price increase,,",
            "- Wholesale price decrease,,",
            "- Discounted listed product with a limited availability,,",
            "- Pending de-list,,",
            "\"If any of the above sections are blank, (i.e. no discounted products for a specified period), the section will not be displayed in\",,",
            "the report.,,",
            "1,,",
            $"RptID: {p.ReportId},,,,,{date},{time},",
        }) sb.Append(line).Append("\r\n");
    }

    /// <summary>
    /// Writes to a temporary file, reads it back, and only then renames it, so Estream can never pick up a
    /// partial or unverified file. Throws and leaves no output if the read-back differs.
    /// </summary>
    public static void WriteAtomic(string path, IReadOnlyList<BatchRow> rows, CsvProfile profile, DateTime when, DateOnly effective)
    {
        if (rows.Count == 0) throw new InvalidOperationException("A batch needs at least one row.");
        var bytes = Write(rows, profile, when, effective);
        var parsed = Parse(bytes);
        var problems = new List<string>(parsed.Errors);
        if (parsed.Items.Count != rows.Count) problems.Add($"Wrote {rows.Count} rows but read back {parsed.Items.Count}.");
        else
        {
            // The same order the writer produced: grouped by category within each section.
            var ordered = rows.Where(r => r.Diff >= 0).GroupBy(r => r.Category).SelectMany(g => g)
                .Concat(rows.Where(r => r.Diff < 0).GroupBy(r => r.Category).SelectMany(g => g)).ToList();
            for (int i = 0; i < ordered.Count; i++)
                if (parsed.Items[i].Upc != ordered[i].Upc || parsed.Items[i].Sku != ordered[i].Sku
                    || parsed.Items[i].NewPrice != ordered[i].NewPrice || parsed.Items[i].OldPrice != ordered[i].OldPrice)
                    problems.Add($"Row {i + 1} changed on read-back ({ordered[i].Name}).");
        }
        if (problems.Count > 0) throw new InvalidDataException("CSV failed its read-back check: " + string.Join(" ", problems));

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Matches the real file: positives as shortest number (27.4, 2), negatives with two decimals (-1.00).</summary>
    public static string Num(decimal d) => d < 0
        ? d.ToString("0.00", CultureInfo.InvariantCulture)
        : d.ToString("0.########", CultureInfo.InvariantCulture);

    /// <summary>Single sizes appear as 0.75 in the real file. Pack sizes keep their text, as in "6 x .355".</summary>
    public static string Container(string c) =>
        c.Length > 0 && Numeric().IsMatch(c) ? Num(D(c)) : c;

    private static string Ascii(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch < 128 ? ch : '?');
        }
        return sb.ToString();
    }

    private static string Quote(string c) =>
        c.Contains(',') || c.Contains('"') || c.Contains('\n') ? $"\"{c.Replace("\"", "\"\"")}\"" : c;

    private static decimal D(string s) => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static IEnumerable<List<string>> ParseRows(string text)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n')
            {
                row.Add(cell.ToString().TrimEnd('\r')); cell.Clear();
                yield return row;
                row = [];
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); yield return row; }
    }
}
