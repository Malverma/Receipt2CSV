using PDFtoImage;
using POSscan.Core;
using RapidOcrNet;
using SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;

namespace POSscan.Ocr;

/// <summary>One page, read the right way up. Line positions are relative to the page.</summary>
public sealed record ScannedPage(int Index, bool Flipped, IReadOnlyList<OcrLine> Lines, string? Barcode);

/// <summary>Renders a PDF and reads it locally. Nothing leaves the PC.</summary>
public sealed class ReceiptOcr : IDisposable
{
    public const int Dpi = 300;
    private const int StripHeight = 1000;
    private const int StripOverlap = 120;
    private const float StampHeight = 3f;
    private readonly RapidOcr _ocr = new();

    public ReceiptOcr()
    {
        // Absolute paths: the library's default is relative to the current folder, which changes
        // when a Windows user opens a file through a shortcut or file association.
        var models = Path.Combine(AppContext.BaseDirectory, "models", "v5");
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
        _ocr.InitModels(
            Path.Combine(models, "ch_PP-OCRv5_mobile_det.onnx"),
            Path.Combine(models, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            Path.Combine(models, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
            Path.Combine(models, "ppocrv5_latin_dict.txt"),
            options);
    }

    public List<ScannedPage> Read(string pdfPath)
    {
        var bytes = File.ReadAllBytes(pdfPath);
        var pages = new List<ScannedPage>();
        int count = Conversion.GetPageCount(bytes);
        for (int page = 0; page < count; page++)
        {
            using var bmp = Conversion.ToImage(bytes, page: page, options: new RenderOptions(Dpi: Dpi, WithAnnotations: false));
            var boxes = ReadStrips(bmp);
            bool flipped = boxes.Count(b => b.Angle == 1) * 2 > boxes.Count;
            if (flipped)
            {
                // The OCR turns each upside-down word around but keeps the page order, so the page has to be read again.
                using var turned = Flip(bmp);
                pages.Add(new(page, true, ToLines(ReadStrips(turned)), ReadTransactionBarcode(turned)));
            }
            else pages.Add(new(page, false, ToLines(boxes), ReadTransactionBarcode(bmp)));
        }
        return pages;
    }

    public static SKBitmap Flip(SKBitmap source)
    {
        var turned = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(turned);
        canvas.RotateDegrees(180, source.Width / 2f, source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return turned;
    }

    // Long receipts are read in overlapping strips, because the detector shrinks tall images and loses small text.
    private List<Box> ReadStrips(SKBitmap page)
    {
        var boxes = new List<Box>();
        for (int y = 0; y < page.Height; y += StripHeight - StripOverlap)
        {
            int h = Math.Min(StripHeight, page.Height - y);
            if (h < 40) break;
            using var strip = new SKBitmap();
            if (!page.ExtractSubset(strip, SKRectI.Create(0, y, page.Width, h))) continue;
            using var copy = strip.Copy(SKColorType.Rgba8888);
            var result = _ocr.Detect(copy, RapidOcrOptions.Default);
            foreach (var b in result.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(b.Text)) continue;
                var pts = b.BoxPoints;
                var score = b.CharScores is { Length: > 0 } s ? s.Average() : 0f;
                float run = pts[1].X - pts[0].X;
                float slope = run > 0 ? (pts[1].Y - pts[0].Y) / run : 0f;
                boxes.Add(new Box(pts.Min(p => p.X), pts.Min(p => p.Y) + y, pts.Max(p => p.X), pts.Max(p => p.Y) + y, b.Text.Trim(), score, slope, b.AngleIndex));
            }
            if (y + h >= page.Height) break;
        }
        return Dedupe(boxes);
    }

    // A line in the overlap is read twice. Keep the copy not cut by a strip edge, which is the taller one.
    private static List<Box> Dedupe(List<Box> boxes)
    {
        var kept = new List<Box>();
        foreach (var b in boxes.OrderByDescending(b => b.Bottom - b.Top).ThenByDescending(b => b.Score))
        {
            bool dup = kept.Any(k =>
            {
                float ox = Math.Min(k.Right, b.Right) - Math.Max(k.Left, b.Left);
                float oy = Math.Min(k.Bottom, b.Bottom) - Math.Max(k.Top, b.Top);
                if (ox <= 0 || oy <= 0) return false;
                return ox / Math.Min(k.Right - k.Left, b.Right - b.Left) > 0.5f
                    && oy / Math.Min(k.Bottom - k.Top, b.Bottom - b.Top) > 0.5f;
            });
            if (!dup) kept.Add(b);
        }
        return kept;
    }

    // A receipt fed in at a slant puts the amounts a line away from their item, so rows are built on levelled positions.
    private static float PageSlope(List<Box> boxes)
    {
        var slopes = boxes.Where(b => b.Right - b.Left > 100).Select(b => b.Slope).OrderBy(s => s).ToList();
        return slopes.Count == 0 ? 0f : slopes[slopes.Count / 2];
    }

    private static List<OcrLine> ToLines(List<Box> boxes)
    {
        // Row spacing comes from typical text height. A stamp across the receipt reads as one giant box, which is dropped.
        var heights = boxes.Select(b => b.Bottom - b.Top).OrderBy(h => h).ToList();
        float typical = heights.Count == 0 ? 0f : heights[heights.Count / 2];
        boxes = boxes.Where(b => b.Bottom - b.Top <= typical * StampHeight).ToList();
        float slope = PageSlope(boxes);
        float Level(Box b) => (b.Top + b.Bottom) / 2 - slope * (b.Left + b.Right) / 2;
        var lines = new List<OcrLine>();
        var row = new List<Box>();
        void Flush()
        {
            if (row.Count == 0) return;
            row.Sort((a, b) => a.Left.CompareTo(b.Left));
            lines.Add(new OcrLine(string.Join(' ', row.Select(b => b.Text)), row.Min(b => b.Score),
                (int)row.Min(b => b.Top), (int)row.Max(b => b.Bottom)));
            row.Clear();
        }
        foreach (var b in boxes.OrderBy(Level))
        {
            if (row.Count > 0)
            {
                float mid = row.Average(Level);
                // Two boxes stacked in the same column are never on one row, however tight the line spacing.
                bool stacked = row.Any(x => Math.Min(x.Right, b.Right) - Math.Max(x.Left, b.Left) > 0.3f * Math.Min(x.Right - x.Left, b.Right - b.Left));
                if (stacked || Level(b) - mid > typical * 0.55f) Flush();
            }
            row.Add(b);
        }
        Flush();
        return lines;
    }

    private static string? ReadTransactionBarcode(SKBitmap page)
    {
        try
        {
            var reader = new BarcodeReader
            {
                AutoRotate = false,
                Options = { TryHarder = true, PossibleFormats = [BarcodeFormat.CODE_128, BarcodeFormat.ITF, BarcodeFormat.CODE_39] },
            };
            using var ptr = page.Copy(SKColorType.Rgba8888);
            var results = reader.DecodeMultiple(ptr);
            return results?.Select(r => r.Text).FirstOrDefault(t => t.Length >= 20 && t.All(char.IsAsciiDigit));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null; // Duplicate detection falls back to the file hash and OCR text.
        }
    }

    public void Dispose() => _ocr.Dispose();

    private sealed record Box(float Left, float Top, float Right, float Bottom, string Text, float Score, float Slope, int Angle);
}
