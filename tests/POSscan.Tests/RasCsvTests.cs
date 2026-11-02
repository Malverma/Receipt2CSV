using System.Text;
using POSscan.Core;

namespace POSscan.Tests;

public class RasCsvTests : IDisposable
{
    private static readonly string Known = Path.Combine("fixtures", "ras-known-good.csv");
    private static readonly DateTime When = new(2017, 9, 28, 8, 49, 55);
    private static readonly DateOnly Effective = new(2017, 10, 29);
    private readonly string _dir = Directory.CreateTempSubdirectory("posscan-test").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Reads_all_rows_of_the_real_file_despite_shifting_columns()
    {
        var f = RasCsv.Read(Known);
        Assert.Empty(f.Errors);
        Assert.Equal(203, f.Items.Count);
        Assert.Equal(114, f.Items.Count(i => i.Section == RasCsv.SectionIncrease));
        Assert.Equal(75, f.Items.Count(i => i.Section == RasCsv.SectionDecrease));
        Assert.Equal(14, f.Items.Count(i => i.Section == RasCsv.SectionDelist));
    }

    [Fact]
    public void Reads_rows_with_a_gap_a_split_name_and_a_blank_upc()
    {
        var items = RasCsv.Read(Known).Items;

        var gap = items.First(i => i.Sku == "125");
        Assert.Equal((27.40m, 26.64m, 0.76m, "1.14"), (gap.NewPrice, gap.OldPrice, gap.Diff, gap.Container));

        var split = items.First(i => i.Sku == "7617");
        Assert.Equal("CHIVAS REGAL 12 YR. OLD", split.Name);
        Assert.Equal(41.54m, split.NewPrice);

        Assert.Equal("", items.First(i => i.Sku == "579037").Upc);
        Assert.Equal("COPPER MOON CABERNET SAUVIGNON", items.First(i => i.Sku == "893289").Name);
        Assert.Equal("Spirits - Whisky", gap.Category);
    }

    [Theory]
    [InlineData(27.4, "27.4")]
    [InlineData(2, "2")]
    [InlineData(0.76, "0.76")]
    [InlineData(0, "0")]
    [InlineData(-1, "-1.00")]
    [InlineData(-0.09, "-0.09")]
    public void Numbers_match_the_real_file(double value, string expected) =>
        Assert.Equal(expected, RasCsv.Num((decimal)value));

    [Theory]
    [InlineData(".750", "0.75")]
    [InlineData("1.140", "1.14")]
    [InlineData("4.000", "4")]
    [InlineData("6 x .355", "6 x .355")]
    public void Containers_match_the_real_file(string input, string expected) =>
        Assert.Equal(expected, RasCsv.Container(input));

    private static byte[] Sample() => RasCsv.Write(
        [new("G", "125", "80686821311", "CANADIAN CLUB - PREMIUM", "1.140", 27.40m, 26.64m, "Spirits - Whisky"),
         new("G", "3558", "83664870387", "GIBSONS FINEST 12 YR", ".750", 19.92m, 21.92m, "Spirits - Whisky")],
        new CsvProfile(), When, Effective);

    [Fact]
    public void Written_file_has_the_same_preamble_bytes_as_the_real_file()
    {
        var known = File.ReadAllText(Known, Encoding.ASCII).Split("\r\n");
        var mine = Encoding.ASCII.GetString(Sample()).Split("\r\n");
        // Line 21 is the second report header, which differs only in trailing commas between files.
        for (int i = 0; i < 20; i++) Assert.Equal(known[i], mine[i]);
    }

    [Fact]
    public void Written_item_rows_are_byte_identical_to_clean_rows_in_the_real_file()
    {
        var known = File.ReadAllText(Known, Encoding.ASCII);
        var mine = Encoding.ASCII.GetString(Sample());
        Assert.Contains("G,3558,83664870387,GIBSONS FINEST 12 YR,0.75,19.92,21.92,-2.00\r\n", known);
        Assert.Contains("G,3558,83664870387,GIBSONS FINEST 12 YR,0.75,19.92,21.92,-2.00\r\n", mine);
    }

    [Fact]
    public void Output_is_ascii_with_crlf_and_no_bom()
    {
        var bytes = Sample();
        Assert.All(bytes, b => Assert.True(b < 128));
        Assert.Equal((byte)'N', bytes[0]);
        var text = Encoding.ASCII.GetString(bytes);
        Assert.EndsWith("\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public void Round_trip_of_every_real_row_keeps_all_values()
    {
        var source = RasCsv.Read(Known).Items.Where(i => i.Section != RasCsv.SectionDelist).ToList();
        var rows = source.Select(i => new BatchRow(i.ListingCode, i.Sku, i.Upc, i.Name, i.Container, i.NewPrice, i.OldPrice, i.Category)).ToList();

        var path = Path.Combine(_dir, "round.csv");
        RasCsv.WriteAtomic(path, rows, new CsvProfile(), When, Effective);
        var back = RasCsv.Read(path);

        Assert.Empty(back.Errors);
        Assert.Equal(rows.Count, back.Items.Count);
        foreach (var r in rows)
            Assert.Contains(back.Items, b => b.Sku == r.Sku && b.Upc == r.Upc && b.NewPrice == r.NewPrice && b.OldPrice == r.OldPrice && b.Name == r.Name);
    }

    [Fact]
    public void Blank_listing_and_sku_rows_can_be_read_back()
    {
        var path = Path.Combine(_dir, "grocery.csv");
        RasCsv.WriteAtomic(path, [new("", "", "68700112003", "MILK, HOMO", "", 5.05m, 5.05m, "DAIRY")], new CsvProfile(), When, Effective);
        var item = RasCsv.Read(path).Items.Single();
        Assert.Equal("68700112003", item.Upc);
        Assert.Equal(5.05m, item.NewPrice);
    }

    [Fact]
    public void Existing_files_are_never_overwritten_and_no_temp_file_remains()
    {
        var path = Path.Combine(_dir, "batch.csv");
        BatchRow[] rows = [new("G", "1", "80686821311", "A", "0.75", 2m, 1m)];
        RasCsv.WriteAtomic(path, rows, new CsvProfile(), When, Effective);
        var before = File.ReadAllBytes(path);
        Assert.ThrowsAny<IOException>(() => RasCsv.WriteAtomic(path, rows, new CsvProfile(), When.AddDays(1), Effective));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void An_empty_batch_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => RasCsv.WriteAtomic(Path.Combine(_dir, "x.csv"), [], new CsvProfile(), When, Effective));

    [Fact]
    public void Non_ascii_names_are_converted_safely()
    {
        var bytes = RasCsv.Write([new("", "1", "", "CRÈME BRÛLÉE", "", 1m, 1m)], new CsvProfile(), When, Effective);
        Assert.Contains("CREME BRULEE", Encoding.ASCII.GetString(bytes));
    }
}
