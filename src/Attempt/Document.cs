namespace Lsquared.Anonymizer;

using System.Diagnostics;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using PdfFontDetails = UglyToad.PdfPig.PdfFonts.FontDetails;
using PdfLetter = UglyToad.PdfPig.Content.Letter;
using PdfTextBlock = UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock;
using PdfTextLine = UglyToad.PdfPig.DocumentLayoutAnalysis.TextLine;
using PdfWord = UglyToad.PdfPig.Content.Word;

public sealed class PDFParser
{
    public Document Parse(string path)
    {
        FileInfo file = new(path);
        return Parse(file);
    }

    public Document Parse(FileInfo file)
    {
        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        return Parse(stream);
    }

    public Document Parse(FileStream stream) =>
        Parse((Stream)stream);

    public Document Parse(Stream stream)
    {
        _ = this;
        using var doc = PdfDocument.Open(stream);
        return ParseDocument(doc);
    }

    /// <summary>
    /// Parses an already-open <see cref="PdfDocument"/> without disposing it, so the caller can
    /// keep using it afterwards (e.g. to correlate the resulting <see cref="Letter.Native"/> references
    /// back to <see cref="UglyToad.PdfPig.Content.Page.Letters"/> for redaction).
    /// </summary>
    public Document Parse(PdfDocument doc)
    {
        _ = this;
        return ParseDocument(doc);
    }

    private static Document ParseDocument(PdfDocument doc)
    {
        List<Page> pages = new(doc.NumberOfPages);
        foreach (var sourcePage in doc.GetPages())
        {
            var letters = DuplicateOverlappingTextProcessor.Get(sourcePage.Letters);
            var words = ExtractWords(letters);
            var textBlocks = SegmentTextBlocks(words);
            var pageSize = CreateSize(sourcePage.Width, sourcePage.Height);
            var page = CreatePage(sourcePage.Number, pageSize, textBlocks, sourcePage.Letters);
            pages.Add(page);
        }
        return new(pages);
    }

    private static IEnumerable<PdfWord> ExtractWords(IReadOnlyList<PdfLetter> letters)
    {
        var wordExtractorOptions = new NearestNeighbourWordExtractor.NearestNeighbourWordExtractorOptions()
        {
            MaxDegreeOfParallelism = Debugger.IsAttached ? 1 : -1,
            Filter = SplitWords,
        };
        NearestNeighbourWordExtractor wordExtractor = new(wordExtractorOptions);
        var words = wordExtractor.GetWords(letters);
        foreach (var group in GroupWordsBetweenWhiteSpaces(words))
        {
            if (ShouldGroupWords(group))
            {
                yield return new PdfWord([.. group.SelectMany(w => w.Letters)]);
            }
            else
            {
                foreach (var word in group)
                    yield return word;
            }
        }

        static bool SplitWords(PdfLetter pivot, PdfLetter candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.Value)) return false;

            if (SameWordChars.Contains(pivot.Value) && SameWordChars.Contains(candidate.Value)) return true;

            if (PunctuationAfter.Contains(candidate.Value)) return false;
            if (PunctuationBefore.Contains(pivot.Value)) return false;

            // check for height difference
            var maxHeight = Math.Max(pivot.PointSize, candidate.PointSize);
            var minHeight = Math.Min(pivot.PointSize, candidate.PointSize);
            if (minHeight != 0 && maxHeight / minHeight > 2.0)
            {
                // pivot and candidate letters cannot belong to the same word 
                // if one letter is more than twice the size of the other.
                return false;
            }

            // check for color difference
            var pivotRgb = pivot.Color.ToRGBValues();
            var candidateRgb = candidate.Color.ToRGBValues();
            if (!pivotRgb.Equals(candidateRgb))
            {
                // pivot and candidate letters cannot belong to the same word 
                // if they don't have the same color.
                return false;
            }

            return true;

        }

        static IEnumerable<List<PdfWord>> GroupWordsBetweenWhiteSpaces(IEnumerable<PdfWord> rawWords, int maxMemoized = 20)
        {
            List<PdfWord> memoized = new(20);
            foreach (var word in rawWords)
            {
                memoized.Add(word);

                if (string.IsNullOrWhiteSpace(word.Text))
                {
                    if (memoized.Count > 1) yield return memoized[..^1];
                    yield return [word];
                    memoized = new(20);
                }

                if (memoized.Count >= maxMemoized)
                {
                    yield return memoized;
                    memoized = new(20);
                }
            }

            if (memoized.Count > 0)
                yield return memoized;
        }

        static bool ShouldGroupWords(List<PdfWord> words)
        {
            if (words.Count < 2) return false;
            var texts = words.Select(w => w.Text);

            // Single French word with apostrophe! -> use dictionary instead?
            if (words.Count is 2 &&
                string.Equals(words[0].Text, "aujourd’") &&
                string.Equals(words[1].Text, "hui"))
            {
                return true;
            }

            // Acronyms e.g. "etc" + "." but not "USA" + "."
            if (words.Count is 2 &&
                words[0].Text.Length is < 4 &&
                !words[0].Text.All(char.IsUpper) &&
                words[1].Text is var text && text.Length is 1 && text[0] is '.')
            {
                return true;
            }

            // URI with no scheme
            if (words[0].Text is "//")
                return true;

            // URI or e-mail with domain-like last segment!
            if (words[^1].Text is var text2 && text2.Length is > 1 && text2[0] is '.')
                return true;

            // URIs & e-mails
            return
                texts.Contains("@") ||
                texts.Contains("http") ||
                texts.Contains("https") ||
                texts.Contains("://") ||
                texts.Contains("www") ||
                texts.Any(t => t.Length > 1 && t[0] is '.');
        }
    }

    private static IEnumerable<PdfTextBlock> SegmentTextBlocks(IEnumerable<PdfWord> words)
    {
        var pageSegmenterOptions = new DocstrumBoundingBoxes.DocstrumBoundingBoxesOptions()
        {
            MaxDegreeOfParallelism = Debugger.IsAttached ? 1 : -1,
            BetweenLineBinSize = 8,
            BetweenLineMultiplier = 1.65,
            WithinLineBinSize = 5,
            WithinLineMultiplier = 4.5,
        };
        DocstrumBoundingBoxes pageSegmenter = new(pageSegmenterOptions);
        var textBlocks = pageSegmenter.GetBlocks(words);
        var readingOrder = UnsupervisedReadingOrderDetector.Instance;
        var orderedTextBlocks = readingOrder.Get(textBlocks);
        return orderedTextBlocks; //.SelectMany(SplitBlockByFont);
    }

    //private static IEnumerable<PdfTextBlock> SplitBlockByFont(PdfTextBlock block)
    //{
    //    List<PdfTextLine> currentLines = [block.TextLines[0]];
    //    PdfFontDetails? currentFont = GetFirstLetterFont(block.TextLines[0]);

    //    for (var i = 1; i < block.TextLines.Count; i++)
    //    {
    //        var line = block.TextLines[i];
    //        var font = GetFirstLetterFont(line);

    //        if (!Equals(font, currentFont))
    //        {
    //            yield return new PdfTextBlock(currentLines, block.Separator);
    //            currentLines = [];
    //            currentFont = font;
    //        }

    //        currentLines.Add(line);
    //    }

    //    yield return new PdfTextBlock(currentLines, block.Separator);
    //}

    private static PdfFontDetails? GetFirstLetterFont(PdfTextLine line) =>
        line.Words[0].Letters[0].FontDetails;

    private static Page CreatePage(int number, Size size, IEnumerable<PdfTextBlock> textBlocks, IReadOnlyList<PdfLetter> nativeLetters) =>
        new(number, size, [.. textBlocks.Select(CreateBlock)], nativeLetters, "\n");

    private static TextBlock CreateBlock(PdfTextBlock sourceBlock) =>
        new([.. sourceBlock.TextLines.Select(CreateLine)], "\n");

    private static TextLine CreateLine(PdfTextLine sourceLine) =>
        new([.. sourceLine.Words.Select(CreateWord)], " ");

    private static Word CreateWord(PdfWord sourceWord) =>
        new([.. sourceWord.Letters.Select(CreateLetter)]);

    private static Letter CreateLetter(PdfLetter sourceLetter)
    {
        var font = new Font(sourceLetter.FontName ?? "?", new FontDetails(sourceLetter.FontDetails.Weight, sourceLetter.FontDetails.IsBold, sourceLetter.FontDetails.IsItalic));
        var decoration = new TextDecoration(null, FontWeight.Normal, default, default);
        return new Letter(sourceLetter.Value, font, sourceLetter.PointSize, decoration, CreateRect(sourceLetter.BoundingBox), CreateRect(sourceLetter.GlyphRectangleLoose), sourceLetter);
    }

    private static Rect CreateRect(PdfRectangle rect) =>
        new(rect.Left, rect.Top, rect.Width, rect.Height);

    private static Size CreateSize(double width, double height) =>
        new(width, height);

    private static readonly HashSet<string> PunctuationAfter = [".", ",", ":", ";", "/", ")", "”"];
    private static readonly HashSet<string> PunctuationBefore = ["’", "/", "(", "“"];
    private static readonly HashSet<string> SameWordChars = [":", "/"];
    private static readonly NearestNeighbourWordExtractor WordExtractor = new(new NearestNeighbourWordExtractor.NearestNeighbourWordExtractorOptions()
    {
        MaxDegreeOfParallelism = Debugger.IsAttached ? 1 : -1,
        Filter = (pivot, candidate) =>
        {
            if (string.IsNullOrWhiteSpace(candidate.Value)) return false;

            if (SameWordChars.Contains(pivot.Value) && SameWordChars.Contains(candidate.Value)) return true;

            if (PunctuationAfter.Contains(candidate.Value)) return false;
            if (PunctuationBefore.Contains(pivot.Value)) return false;

            // check for height difference
            var maxHeight = Math.Max(pivot.PointSize, candidate.PointSize);
            var minHeight = Math.Min(pivot.PointSize, candidate.PointSize);
            if (minHeight != 0 && maxHeight / minHeight > 2.0)
            {
                // pivot and candidate letters cannot belong to the same word 
                // if one letter is more than twice the size of the other.
                return false;
            }

            // check for color difference
            var pivotRgb = pivot.Color.ToRGBValues();
            var candidateRgb = candidate.Color.ToRGBValues();
            if (!pivotRgb.Equals(candidateRgb))
            {
                // pivot and candidate letters cannot belong to the same word 
                // if they don't have the same color.
                return false;
            }

            return true;
        }
    });
}

public sealed class Document
{
    public IReadOnlyList<Page> Pages { get; }
    public Document(IReadOnlyList<Page> pages)
    {
        if (pages.Count is 0) throw new ArgumentException("Empty pages provided.", nameof(pages));

        Pages = pages;
    }
}

public sealed class Page
{
    public int Number { get; }
    public Size Size { get; }
    public string Text { get; }
    public IReadOnlyList<TextBlock> TextBlocks { get; }

    /// <summary>
    /// The page's letters exactly as produced by the single PdfPig parse pass that built this
    /// <see cref="Document"/> (i.e. the same object instances as <see cref="Letter.Native"/> below),
    /// in original content-stream order. Calling <c>PdfDocument.GetPage</c> again would re-parse the
    /// page and return different object instances, breaking reference-based redaction matching.
    /// </summary>
    public IReadOnlyList<PdfLetter> NativeLetters { get; }

    public Page(int number, Size size, IReadOnlyList<TextBlock> blocks, IReadOnlyList<PdfLetter> nativeLetters, string separator = "\n")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1, nameof(number));
        if (blocks.Count is 0) throw new ArgumentException("Empty text blocks provided.", nameof(blocks));

        Number = number;
        TextBlocks = blocks;
        NativeLetters = nativeLetters;
        Text = string.Join(separator, TextBlocks.Select(tb => tb.Text));
    }
    public override string ToString() => Text;

    /// <summary>
    /// Returns the letters covering the [start, end) range of <see cref="Text"/>, grouped by
    /// text line, walking the same block/line/word separators used to build <see cref="Text"/>.
    /// Grouping by line keeps redaction boxes tight when an entity spans a line break.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Letter>> GetLettersByLine(int start, int end)
    {
        List<IReadOnlyList<Letter>> result = new();
        var pos = 0;
        for (var b = 0; b < TextBlocks.Count; b++)
        {
            var block = TextBlocks[b];
            for (var li = 0; li < block.Lines.Count; li++)
            {
                var line = block.Lines[li];
                List<Letter> lineLetters = new();
                for (var wi = 0; wi < line.Words.Count; wi++)
                {
                    var word = line.Words[wi];
                    foreach (var letter in word.Letters)
                    {
                        if (pos >= start && pos < end) lineLetters.Add(letter);
                        pos++;
                    }
                    if (wi < line.Words.Count - 1) pos++; // word separator
                }
                if (lineLetters.Count > 0) result.Add(lineLetters);
                if (li < block.Lines.Count - 1) pos++; // line separator
            }
            if (b < TextBlocks.Count - 1) pos++; // block separator
        }
        return result;
    }
}

public sealed class TextBlock
{
    public Rect BoundingBox { get; }
    public string Text { get; }
    public IReadOnlyList<TextLine> Lines { get; }
    public TextBlock(IReadOnlyList<TextLine> lines, string separator = "\n")
    {
        if (lines.Count is 0) throw new ArgumentException("Empty lines provided.", nameof(lines));

        Lines = lines;
        Text = string.Join(separator, Lines.Select(l => l.Text));
        BoundingBox = new Rect(
            Lines.Min(l => l.BoundingBox.Left),
            Lines.Min(l => l.BoundingBox.Top),
            Lines.Max(l => l.BoundingBox.Right) - Lines.Min(l => l.BoundingBox.Left),
            Lines.Max(l => l.BoundingBox.Bottom) - Lines.Min(l => l.BoundingBox.Top)
        );
    }
    public override string ToString() => Text;
}

public sealed class TextLine
{
    public Rect BoundingBox { get; }
    public string Text { get; }
    public IReadOnlyList<Word> Words { get; }
    public TextLine(IReadOnlyList<Word> words, string separator = " ")
    {
        if (words.Count is 0) throw new ArgumentException("Empty words provided.", nameof(words));

        Words = words;
        Text = string.Join(separator, Words.Select(w => w.Text));
        BoundingBox = new Rect(
            Words.Min(w => w.BoundingBox.Left),
            Words.Min(w => w.BoundingBox.Top),
            Words.Max(w => w.BoundingBox.Right) - Words.Min(w => w.BoundingBox.Left),
            Words.Max(w => w.BoundingBox.Bottom) - Words.Min(w => w.BoundingBox.Top)
        );
    }
    public override string ToString() => Text;
}

public sealed class Word
{
    public Rect BoundingBox { get; }
    public string FontName { get; }
    public IReadOnlyList<Letter> Letters { get; }
    public string Text { get; }
    public Word(IReadOnlyList<Letter> letters)
    {
        if (letters.Count is 0) throw new ArgumentException("Empty letters provided.", nameof(letters));

        Letters = letters;
        Text = string.Concat(Letters.Select(l => l.Value));
        BoundingBox = new Rect(
            Letters.Min(l => l.BoundingBox.Left),
            Letters.Min(l => l.BoundingBox.Top),
            Letters.Max(l => l.BoundingBox.Right) - Letters.Min(l => l.BoundingBox.Left),
            Letters.Max(l => l.BoundingBox.Bottom) - Letters.Min(l => l.BoundingBox.Top)
        );
        FontName = Letters[0].Font.Name ?? string.Empty;
    }
    public override string ToString() => Text;
}

public sealed record class Letter(string Value, Font Font, double FontSize, TextDecoration Decoration, Rect BoundingBox, Rect GlyphBox, PdfLetter Native)
{
    public override string ToString() => Value;
}

public sealed record class Font(string Name, FontDetails Details)
{
    public override string ToString() => $"Font: {Name}";
}

public sealed record class FontDetails(int Weight, bool IsBold, bool IsItalic);

public enum FontWeight { Thin = 100, ExtraLight = 200, Light = 300, Regular = 400, Medium = 500, SemiBold = 600, Bold = 700, ExtraBold = 800, Black = 900, Normal = Medium, Heavy = Black }

public enum FontStyle { Normal, Italic, Oblique }

public enum TextEffect { None, Underline, Strikethrough, Superscript, Subscript }

public sealed record class TextDecoration(IColor? Color, FontWeight Weight, FontStyle Style, TextEffect Effect);

public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public Rect(Point topLeft, Size size) : this(topLeft.X, topLeft.Y, size.Width, size.Height) { }
    public Size Size => new(Width, Height);
    public double Left => X;
    public double Right => X + Width;
    public double Top => Y;
    public double Bottom => Y + Height;
    public Point TopLeft => new(X, Y);
    public Point TopRight => new(X + Width, Y);
    public Point BottomLeft => new(X, Y + Height);
    public Point BottomRight => new(X + Width, Y + Height);
}

public readonly record struct Point(double X, double Y);

public readonly record struct Size(double Width, double Height);

public interface IColor
{
    ColorSpace ColorSpace { get; }
}

public enum ColorSpace
{
    Grayscale = 0,
    Rgb = 1,
    CMYK = 2,
    CIEGray = 3,
    CIERgb = 4,
    CIE = 5,
    ICC = 6,
    Indexed = 7,
    Pattern = 8,
    Separation = 9,
    DeviceN = 10
}