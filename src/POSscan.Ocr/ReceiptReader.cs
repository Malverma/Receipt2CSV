using PDFtoImage;
using POSscan.Core;
using POSscan.Data;
using SkiaSharp;

namespace POSscan.Ocr;

/// <summary>Turns a PDF into OCR lines plus the page image shown on the review screen.</summary>
public sealed class ReceiptReader : IReceiptReader, IDisposable
{
    private readonly Lazy<ReceiptOcr> _ocr = new(() => new ReceiptOcr());

    public IReadOnlyList<ReaderResult> Read(string pdfPath)
    {
        try
        {
            var pages = _ocr.Value.Read(pdfPath);
            if (pages.All(p => p.Lines.Count == 0)) throw new ReceiptReadException("No text could be found in this PDF. Check that the scan isn't blank.");
            var bytes = File.ReadAllBytes(pdfPath);
            var results = new List<ReaderResult>();
            foreach (var receipt in ReceiptSplitter.Split(pages.Select(p => p.Lines).ToList()))
            {
                var used = receipt.Select(part => pages[part.Page]).DistinctBy(p => p.Index).ToList();
                var (png, width, height, tops) = Preview(bytes, used);
                var lines = receipt.SelectMany(part => part.Lines.Select(l =>
                    l with { Top = l.Top + tops[part.Page], Bottom = l.Bottom + tops[part.Page] })).ToList();
                results.Add(new ReaderResult(lines, used.Select(p => p.Barcode).FirstOrDefault(b => b is not null), png, width, height));
            }
            return results;
        }
        catch (ReceiptReadException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ReceiptReadException("This PDF couldn't be read. It may be damaged or password protected.", ex);
        }
    }

    // Rendered at half the OCR resolution: sharp enough to read, small enough to scroll smoothly.
    private static (byte[] Png, int Width, int Height, Dictionary<int, int> Tops) Preview(byte[] bytes, IReadOnlyList<ScannedPage> used)
    {
        var pages = new List<SKBitmap>();
        var tops = new Dictionary<int, int>();
        try
        {
            foreach (var page in used)
            {
                using var render = Conversion.ToImage(bytes, page: page.Index, options: new RenderOptions(Dpi: ReceiptOcr.Dpi / 2, WithAnnotations: false));
                pages.Add(page.Flipped ? ReceiptOcr.Flip(render) : render.Copy());
                tops[page.Index] = pages.Take(pages.Count - 1).Sum(p => p.Height) * 2;
            }
            int w = pages.Max(p => p.Width), h = pages.Sum(p => p.Height);
            using var sheet = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(sheet))
            {
                canvas.Clear(SKColors.White);
                int y = 0;
                foreach (var p in pages) { canvas.DrawBitmap(p, 0, y); y += p.Height; }
            }
            using var image = SKImage.FromBitmap(sheet);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return (data.ToArray(), w * 2, h * 2, tops); // Reported in OCR pixels, the units of each line's position.
        }
        finally
        {
            foreach (var p in pages) p.Dispose();
        }
    }

    public void Dispose()
    {
        if (_ocr.IsValueCreated) _ocr.Value.Dispose();
    }
}
