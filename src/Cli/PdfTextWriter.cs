using PdfSharpCore.Drawing;
using PdfSharpCore.Drawing.Layout;
using PdfSharpCore.Pdf;

namespace Anonymizer.Cli;

internal static class PdfTextWriter
{
    public static void Write(string outputPath, string text)
    {
        using PdfDocument document = new();
        PdfPage page = document.AddPage();
        XGraphics graphics = XGraphics.FromPdfPage(page);
        XFont font = new("DejaVu Sans", 11, XFontStyle.Regular);
        XTextFormatter formatter = new(graphics);

        const double margin = 40d;
        const double lineHeight = 16d;
        double y = margin;
        string[] lines = text.Replace("\r", string.Empty).Split('\n');

        foreach (string line in lines)
        {
            foreach (string wrappedLine in WrapLine(line, graphics, font, page.Width - (margin * 2d)))
            {
                if (y + lineHeight > page.Height - margin)
                {
                    graphics.Dispose();
                    page = document.AddPage();
                    y = margin;
                    graphics = XGraphics.FromPdfPage(page);
                    formatter = new XTextFormatter(graphics);
                }

                formatter.DrawString(wrappedLine, font, XBrushes.Black, new XRect(margin, y, page.Width - (margin * 2d), lineHeight));
                y += lineHeight;
            }

            y += lineHeight * 0.4d;
        }

        graphics.Dispose();
        document.Save(outputPath);
    }

    private static IEnumerable<string> WrapLine(string line, XGraphics graphics, XFont font, double maxWidth)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            yield return string.Empty;
            yield break;
        }

        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 0)
        {
            yield return string.Empty;
            yield break;
        }

        string current = words[0];
        for (int i = 1; i < words.Length; i++)
        {
            string candidate = current + " " + words[i];
            if (graphics.MeasureString(candidate, font).Width <= maxWidth)
            {
                current = candidate;
                continue;
            }

            yield return current;
            current = words[i];
        }

        yield return current;
    }
}
