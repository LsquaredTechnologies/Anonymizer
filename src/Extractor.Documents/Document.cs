namespace Anonymizer.Extractor.Documents;

public sealed class Document
{
	public required PhysicalDocumentTree PhysicalTree { get; init; }
	public required SemanticDocumentTree SemanticTree { get; init; }
}

public sealed record PhysicalDocumentTree(IReadOnlyList<PhysicalPage> Pages);

public sealed record PhysicalPage(
	int PageNumber,
	double Width,
	double Height,
	IReadOnlyList<HorizontalZone> HorizontalZones,
	IReadOnlyList<TableBlock> Tables);

public sealed record HorizontalZone(
	string Id,
	Rectangle Bounds,
	IReadOnlyList<PhysicalColumn> Columns);

public sealed record PhysicalColumn(
	string Id,
	int ColumnIndex,
	Rectangle Bounds,
	IReadOnlyList<VerticalZone> VerticalZones);

public sealed record VerticalZone(
	string Id,
	Rectangle Bounds,
	IReadOnlyList<TextLine> Lines);

public sealed record TextLine(
	string Id,
	int PageNumber,
	string ZoneId,
	Rectangle Bounds,
	IReadOnlyList<TextToken> Tokens,
	string Text);

public sealed record TextToken(
	string Text,
	Rectangle Bounds,
	float FontSize,
	string? FontName);

public sealed record TableBlock(
	string Id,
	int PageNumber,
	Rectangle Bounds,
	IReadOnlyList<TableRow> Rows);

public sealed record TableRow(int RowIndex, IReadOnlyList<TableCell> Cells);

public sealed record TableCell(
	int RowIndex,
	int ColumnIndex,
	Rectangle Bounds,
	string Text,
	IReadOnlyList<TextLine> Lines);

public sealed record SemanticDocumentTree(IReadOnlyList<SemanticParagraph> Paragraphs);

public sealed record SemanticParagraph(
	string Id,
	int PageNumber,
	string ZoneId,
	Rectangle Bounds,
	IReadOnlyList<string> LineIds,
	string Text,
	int? HeadingLevel);

public readonly record struct Rectangle(double X, double Y, double Width, double Height)
{
	public double Right => X + Width;
	public double Bottom => Y - Height;

	public static Rectangle FromLTRB(double left, double top, double right, double bottom)
		=> new(left, top, Math.Max(0d, right - left), Math.Max(0d, top - bottom));

	public static Rectangle Union(IEnumerable<Rectangle> rectangles)
	{
		var items = rectangles.ToArray();
		if (items.Length == 0)
		{
			return new Rectangle(0, 0, 0, 0);
		}

		var left = items.Min(x => x.X);
		var right = items.Max(x => x.Right);
		var top = items.Max(x => x.Y);
		var bottom = items.Min(x => x.Bottom);
		return FromLTRB(left, top, right, bottom);
	}
}
