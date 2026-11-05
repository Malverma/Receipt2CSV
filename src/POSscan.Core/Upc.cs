namespace POSscan.Core;

public static class Upc
{
    /// <summary>GS1 check digit for a code body of any length.</summary>
    public static int CheckDigit(string body)
    {
        int sum = 0, weight = 3;
        for (int i = body.Length - 1; i >= 0; i--)
        {
            if (!char.IsAsciiDigit(body[i])) throw new ArgumentException("Digits only", nameof(body));
            sum += (body[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }
        return (10 - sum % 10) % 10;
    }

    public static bool HasValidCheckDigit(string code) =>
        code.Length >= 2 && code.All(char.IsAsciiDigit) && CheckDigit(code[..^1]) == code[^1] - '0';

    public static CodeKind Classify(string code)
    {
        if (code.Length == 0 || !code.All(char.IsAsciiDigit)) return CodeKind.Unknown;
        return code.Length switch
        {
            11 => CodeKind.Upc11,
            7 => CodeKind.InStore7,
            >= 3 and <= 5 => CodeKind.ShortCode,
            _ => CodeKind.Unknown,
        };
    }

    /// <summary>
    /// Converts an 11-digit receipt code to the form the RAS file uses: check digit added, leading zeros dropped.
    /// The receipt prints no check digit, so this can't detect a misread digit. A result is only
    /// trustworthy after it matches a known item or a person confirms it.
    /// </summary>
    public static string ReceiptCodeToRasUpc(string code)
    {
        if (Classify(code) != CodeKind.Upc11) throw new ArgumentException("Expected an 11-digit code", nameof(code));
        return (code + CheckDigit(code)).TrimStart('0');
    }

    /// <summary>
    /// The number printed under the product's barcode, or null when there isn't one to show. A saved UPC with a valid
    /// check digit wins, padded back to 12 digits (UPC-A) since RAS-style UPCs drop leading zeros. Otherwise an 11-digit
    /// receipt code gets its check digit. In-store, produce, and unreadable codes have no barcode.
    /// </summary>
    public static string? Barcode(string receiptCode, string? savedUpc)
    {
        var upc = savedUpc?.Trim() ?? "";
        if (upc.Length is >= 8 and <= 14 && HasValidCheckDigit(upc)) return upc.Length < 12 ? upc.PadLeft(12, '0') : upc;
        return Classify(receiptCode) == CodeKind.Upc11 ? receiptCode + CheckDigit(receiptCode) : null;
    }
}
