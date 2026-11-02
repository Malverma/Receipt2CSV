using POSscan.Core;

namespace POSscan.Tests;

public class UpcAndMatchTests
{
    [Theory]
    [InlineData("06870011200", "68700112003")]
    [InlineData("06038300291", "60383002916")]
    [InlineData("62883487114", "628834871148")]
    public void Receipt_code_becomes_ras_style_upc(string code, string expected) =>
        Assert.Equal(expected, Upc.ReceiptCodeToRasUpc(code));

    [Fact]
    public void Every_upc_in_the_real_ras_file_has_a_valid_check_digit()
    {
        var upcs = RasCsv.Read(Path.Combine("fixtures", "ras-known-good.csv")).Items.Select(i => i.Upc).Where(u => u.Length > 0).ToList();
        Assert.Equal(201, upcs.Count);
        Assert.All(upcs, u => Assert.True(Upc.HasValidCheckDigit(u), u));
    }

    [Theory]
    [InlineData("06870011200", CodeKind.Upc11)]
    [InlineData("2439450", CodeKind.InStore7)]
    [InlineData("4556", CodeKind.ShortCode)]
    [InlineData("740", CodeKind.ShortCode)]
    [InlineData("06870012.00", CodeKind.Unknown)]
    [InlineData("€2930701501", CodeKind.Unknown)]
    public void Codes_are_classified(string code, CodeKind kind) => Assert.Equal(kind, Upc.Classify(code));

    [Fact]
    public void A_misread_code_is_never_matched_automatically()
    {
        // Real case: OCR read 06038314170 as 06036314170, which is a perfectly valid-looking code.
        var matcher = new ItemMatcher([new("06038314170", "9001", "60383141701", "PC MUSHROOM SLICED 227G")]);
        var result = matcher.Match("06036314170");
        Assert.Equal(MatchKind.NearMiss, result.Kind);
        Assert.Null(result.Mapping);
        Assert.Equal("06038314170", result.NearMisses.Single().ReceiptCode);
    }

    [Fact]
    public void An_exact_confirmed_code_matches()
    {
        var matcher = new ItemMatcher([new("06038314170", "9001", "60383141701", "PC MUSHROOM SLICED 227G")]);
        Assert.Equal(MatchKind.Confirmed, matcher.Match("06038314170").Kind);
        Assert.Equal(MatchKind.None, matcher.Match("99999999999").Kind);
    }
}
