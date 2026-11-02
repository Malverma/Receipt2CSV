namespace POSscan.Core;

public enum MatchKind { Confirmed, NearMiss, None }

/// <summary>A receipt code a person has already linked to an Estream item.</summary>
public sealed record ItemMapping(
    string ReceiptCode, string Sku, string Upc, string EstreamName,
    string ListingCode = "", string Container = "", bool NotForResale = false);

public sealed record MatchResult(MatchKind Kind, ItemMapping? Mapping, IReadOnlyList<ItemMapping> NearMisses);

/// <summary>
/// Only an exact match on a confirmed code is accepted. Receipt codes have no check digit, so a code one
/// digit away from a known item is probably a misread, but that's a question for a person, never a match.
/// </summary>
public sealed class ItemMatcher(IEnumerable<ItemMapping> mappings)
{
    private readonly Dictionary<string, ItemMapping> _byCode = mappings.ToDictionary(m => m.ReceiptCode);

    public MatchResult Match(string code)
    {
        if (_byCode.TryGetValue(code, out var exact)) return new(MatchKind.Confirmed, exact, []);
        var near = _byCode.Values.Where(m => OneCharOff(m.ReceiptCode, code)).ToList();
        return new(near.Count > 0 ? MatchKind.NearMiss : MatchKind.None, null, near);
    }

    private static bool OneCharOff(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int differences = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i] && ++differences > 1) return false;
        return differences == 1;
    }
}
