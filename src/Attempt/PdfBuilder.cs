using Anonymizer.Extractor.PII;

using Lsquared.Anonymizer;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace Lsquared.Anonymizer;

public static class PdfBuilder
{
    public static void Rebuild(Document doc, PdfDocument original, Dictionary<int, List<Entity>> entities, string path) =>
        Rebuild(doc, original, entities, File.Create(path));

    public static void Rebuild(Document doc, PdfDocument original, Dictionary<int, List<Entity>> entities, FileInfo file) =>
        Rebuild(doc, original, entities, file.Open(FileMode.Open, FileAccess.Write, FileShare.None));

    public static void Rebuild(Document doc, PdfDocument original, Dictionary<int, List<Entity>> entities, Stream stream)
    {
        var tokenWriter = new PiiRedactingTokenWriter();
        using (var builder = new PdfDocumentBuilder(stream, false, PdfWriterType.Default, original.Version, tokenWriter: tokenWriter))
        {
            foreach (var page in doc.Pages)
            {
                // Toutes les lettres PII de la page, regroupées par ligne pour des boîtes ajustées, mais
                // aussi rassemblées à plat vers les Letter natifs pour effacer réellement les glyphes.
                var piiLineGroups = entities[page.Number]
                    .SelectMany(entity => page.GetLettersByLine(entity.Start, entity.End))
                    .Where(group => group.Count > 0)
                    .ToList();

                tokenWriter.Page = page.Number;
                tokenWriter.PageLetters = page.NativeLetters;
                tokenWriter.LettersToRedact = piiLineGroups.SelectMany(g => g).Select(l => l.Native).ToHashSet();

                var pageBuilder = builder.AddPage(original, page.Number);
                pageBuilder.SetTextAndFillColor(0, 0, 0);

                //const double padding = 0.5;
                //foreach (var lineLetters in piiLineGroups)
                //{
                //    var left = lineLetters.Min(l => l.BoundingBox.Left);
                //    var right = lineLetters.Max(l => l.BoundingBox.Right);
                //    var top = lineLetters.Max(l => l.BoundingBox.Top);
                //    var bottom = lineLetters.Min(l => l.BoundingBox.Top - l.BoundingBox.Height);

                //    pageBuilder.DrawRectangle(
                //        new PdfPoint(left - padding, bottom - padding),
                //        (right - left) + 2 * padding,
                //        (top - bottom) + 2 * padding,
                //        fill: true);
                //}
            }
        }
    }
}
