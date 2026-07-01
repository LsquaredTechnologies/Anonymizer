using System.Text;
using Anonymizer.Extractor.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Anonymizer.Extractor.Documents.PDF;

public static class PDFDocumentTextExtractor
{
	public static Document Extract(string pdfPath)
	{
		using var pdf = PdfDocument.Open(pdfPath);
		var physicalPages = new List<PhysicalPage>();
		var paragraphAnalyses = new List<ParagraphAnalysis>();

		foreach (var page in pdf.GetPages())
		{
			var tokens = BuildTokensFromLetters(page.Letters).ToList();
			var horizontalZones = BuildHorizontalZones(page.Number, page.Width, tokens);
			var verticalZones = horizontalZones
				.SelectMany(h => h.Columns)
				.SelectMany(c => c.VerticalZones)
				.ToList();
			var tables = DetectTables(page.Number, verticalZones);

			foreach (var zone in verticalZones)
			{
				paragraphAnalyses.AddRange(BuildParagraphs(zone, page.Number));
			}

			physicalPages.Add(new PhysicalPage(
				page.Number,
				page.Width,
				page.Height,
				horizontalZones,
				tables));
		}

		var paragraphs = paragraphAnalyses.Select(x => x.Paragraph).ToList();
		var mergedParagraphs = MergeFragmentedParagraphs(paragraphs).ToList();
		var analysisByParagraphId = paragraphAnalyses
			.ToDictionary(x => x.Paragraph.Id, x => x, StringComparer.Ordinal);
		var headingLevels = AssignHeadingLevels(mergedParagraphs, analysisByParagraphId);
		var finalParagraphs = mergedParagraphs
			.Select(p => p with
			{
				HeadingLevel = headingLevels.TryGetValue(p.Id, out var level) ? level : null
			})
			.ToList();

		return new Document
		{
			PhysicalTree = new PhysicalDocumentTree(physicalPages),
			SemanticTree = new SemanticDocumentTree(finalParagraphs)
		};
	}

	private static IReadOnlyList<SemanticParagraph> MergeFragmentedParagraphs(IReadOnlyList<SemanticParagraph> paragraphs)
	{
		if (paragraphs.Count < 2)
		{
			return paragraphs;
		}

		var result = new List<SemanticParagraph>(paragraphs.Count);
		foreach (var paragraph in paragraphs)
		{
			if (result.Count == 0)
			{
				result.Add(paragraph);
				continue;
			}

			var previous = result[^1];
			if (!ShouldMergeParagraphFragment(previous, paragraph))
			{
				result.Add(paragraph);
				continue;
			}

			var mergedText = (previous.Text + " " + paragraph.Text).Trim();
			var merged = previous with
			{
				Bounds = Rectangle.Union([previous.Bounds, paragraph.Bounds]),
				LineIds = previous.LineIds.Concat(paragraph.LineIds).ToList(),
				Text = mergedText
			};

			result[^1] = merged;
		}

		return result;
	}

	private static bool ShouldMergeParagraphFragment(SemanticParagraph previous, SemanticParagraph current)
	{
		if (previous.PageNumber != current.PageNumber)
		{
			return false;
		}

		var text = current.Text.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		if (IsLikelyStandaloneLine(text))
		{
			return false;
		}

		if (WordCount(text) > 18)
		{
			return false;
		}

		var firstLetter = text.FirstOrDefault(char.IsLetter);
		if (firstLetter == default || !char.IsLower(firstLetter))
		{
			return false;
		}

		var previousText = previous.Text.Trim();
		if (string.IsNullOrWhiteSpace(previousText))
		{
			return false;
		}

		if (previousText.EndsWith(':') || previousText.EndsWith(';'))
		{
			return false;
		}

		if (IsLikelyStandaloneLine(previousText) || IsTitleLikeLine(previousText))
		{
			return false;
		}

		return true;
	}

	private static IEnumerable<TextToken> BuildTokensFromLetters(IEnumerable<Letter> letters)
	{
		var filtered = letters
			.Where(l => !string.IsNullOrEmpty(l.Value))
			.OrderByDescending(l => l.GlyphRectangle.Top)
			.ThenBy(l => l.GlyphRectangle.Left)
			.ToList();

		if (filtered.Count == 0)
		{
			yield break;
		}

		var lineThreshold = Math.Max(1.5d, filtered.Select(l => l.GlyphRectangle.Height).DefaultIfEmpty(8d).Median() * 0.6d);
		var lines = new List<List<Letter>>();
		var lineCenters = new List<double>();

		foreach (var letter in filtered)
		{
			var centerY = letter.GlyphRectangle.Bottom + (letter.GlyphRectangle.Height / 2d);
			var attached = false;
			for (var i = 0; i < lineCenters.Count; i++)
			{
				if (Math.Abs(lineCenters[i] - centerY) <= lineThreshold)
				{
					lines[i].Add(letter);
					lineCenters[i] = (lineCenters[i] + centerY) / 2d;
					attached = true;
					break;
				}
			}

			if (!attached)
			{
				lines.Add([letter]);
				lineCenters.Add(centerY);
			}
		}

		foreach (var line in lines)
		{
			var ordered = line.OrderBy(l => l.GlyphRectangle.Left).ToList();
			var avgWidth = ordered.Select(l => l.GlyphRectangle.Width).DefaultIfEmpty(5d).Median();
			var avgHeight = ordered.Select(l => l.GlyphRectangle.Height).DefaultIfEmpty(10d).Median();
			var positiveGaps = new List<double>();
			for (var i = 1; i < ordered.Count; i++)
			{
				var gap = ordered[i].GlyphRectangle.Left - ordered[i - 1].GlyphRectangle.Right;
				if (gap > 0d)
				{
					positiveGaps.Add(gap);
				}
			}

			var medianGap = positiveGaps.Count > 0 ? positiveGaps.Median() : 0d;
			var wordGap = Math.Max(1.3d, Math.Min(3.8d, Math.Max(avgHeight * 0.22d, Math.Max(avgWidth * 0.35d, medianGap * 2.4d))));

			var current = new List<Letter>();
			for (var i = 0; i < ordered.Count; i++)
			{
				var letter = ordered[i];
				if (string.IsNullOrWhiteSpace(letter.Value))
				{
					if (current.Count > 0)
					{
						yield return ToToken(current);
						current = [];
					}

					continue;
				}

				if (current.Count > 0)
				{
					var previous = ordered[i - 1];
					var gap = letter.GlyphRectangle.Left - previous.GlyphRectangle.Right;
					if (gap >= wordGap)
					{
						yield return ToToken(current);
						current = [];
					}
				}

				current.Add(letter);
			}

			if (current.Count > 0)
			{
				yield return ToToken(current);
			}
		}
	}

	private static TextToken ToToken(List<Letter> letters)
	{
		var text = string.Concat(letters.Select(x => x.Value));
		var left = letters.Min(x => x.GlyphRectangle.Left);
		var right = letters.Max(x => x.GlyphRectangle.Right);
		var top = letters.Max(x => x.GlyphRectangle.Top);
		var bottom = letters.Min(x => x.GlyphRectangle.Bottom);
		var first = letters[0];

		return new TextToken(
			text,
			Rectangle.FromLTRB(left, top, right, bottom),
			    (float)first.PointSize,
			first.FontName);
	}

	private static IReadOnlyList<HorizontalZone> BuildHorizontalZones(int pageNumber, double pageWidth, IReadOnlyList<TextToken> tokens)
	{
		if (tokens.Count == 0)
		{
			return [];
		}

		var pageLines = BuildLines(tokens.ToList(), pageNumber, $"p{pageNumber}-h0-c0-v0");
		var horizontalBands = SplitLinesIntoHorizontalBands(pageLines);
		var horizontalZones = new List<HorizontalZone>(horizontalBands.Count);

		for (var h = 0; h < horizontalBands.Count; h++)
		{
			var bandLines = horizontalBands[h];
			var bandTokens = bandLines
				.SelectMany(l => l.Tokens)
				.OrderByDescending(t => t.Bounds.Y)
				.ThenBy(t => t.Bounds.X)
				.ToList();
			if (bandTokens.Count == 0)
			{
				continue;
			}

			var columns = SplitIntoColumns(pageWidth, bandTokens);
			var physicalColumns = new List<PhysicalColumn>(columns.Count);

			for (var c = 0; c < columns.Count; c++)
			{
				var columnId = $"p{pageNumber}-h{h + 1}-c{c + 1}";
				var columnLines = BuildLines(columns[c], pageNumber, $"{columnId}-v0");
				if (columnLines.Count == 0)
				{
					continue;
				}

				var verticalGroups = SplitLinesIntoVerticalZones(columnLines);
				var verticalZones = new List<VerticalZone>(verticalGroups.Count);
				for (var v = 0; v < verticalGroups.Count; v++)
				{
					var verticalLines = verticalGroups[v]
						.OrderByDescending(l => l.Bounds.Y)
						.ToList();
					if (verticalLines.Count == 0)
					{
						continue;
					}

					verticalZones.Add(new VerticalZone(
						$"{columnId}-v{v + 1}",
						Rectangle.Union(verticalLines.Select(l => l.Bounds)),
						verticalLines));
				}

				if (verticalZones.Count == 0)
				{
					continue;
				}

				physicalColumns.Add(new PhysicalColumn(
					columnId,
					c,
					Rectangle.Union(verticalZones.Select(v => v.Bounds)),
					verticalZones));
			}

			if (physicalColumns.Count == 0)
			{
				continue;
			}

			horizontalZones.Add(new HorizontalZone(
				$"p{pageNumber}-h{h + 1}",
				Rectangle.Union(physicalColumns.Select(c => c.Bounds)),
				physicalColumns));
		}

		return horizontalZones;
	}

	private static List<List<TextToken>> SplitIntoColumns(double pageWidth, IReadOnlyList<TextToken> tokens)
	{
		if (tokens.Count < 8)
		{
			return [tokens.ToList()];
		}

		var centers = tokens
			.Select(t => t.Bounds.X + (t.Bounds.Width / 2d))
			.OrderBy(x => x)
			.ToArray();
		if (centers.Length < 8)
		{
			return [tokens.ToList()];
		}

		var gaps = new List<(int Index, double Gap, double Split)>();
		for (var i = 1; i < centers.Length; i++)
		{
			var gap = centers[i] - centers[i - 1];
			if (gap > 0d)
			{
				gaps.Add((i, gap, centers[i - 1] + (gap / 2d)));
			}
		}

		if (gaps.Count == 0)
		{
			return [tokens.ToList()];
		}

		var medianGap = gaps.Select(g => g.Gap).Median();
		var gutterThreshold = Math.Max(12d, Math.Max(pageWidth * 0.03d, medianGap * 3.8d));
		var splitCandidates = gaps
			.Where(g => g.Gap >= gutterThreshold && g.Split >= pageWidth * 0.05d && g.Split <= pageWidth * 0.95d)
			.OrderByDescending(g => g.Gap)
			.Take(3)
			.Select(g => g.Split)
			.OrderBy(x => x)
			.ToList();

		if (splitCandidates.Count == 0)
		{
			return [tokens.ToList()];
		}

		var mergedSplits = new List<double>();
		for (var i = 0; i < splitCandidates.Count; i++)
		{
			if (mergedSplits.Count == 0)
			{
				mergedSplits.Add(splitCandidates[i]);
				continue;
			}

			var previous = mergedSplits[^1];
			if (Math.Abs(splitCandidates[i] - previous) < pageWidth * 0.04d)
			{
				mergedSplits[^1] = (previous + splitCandidates[i]) / 2d;
			}
			else
			{
				mergedSplits.Add(splitCandidates[i]);
			}
		}

		var columns = Enumerable.Range(0, mergedSplits.Count + 1)
			.Select(_ => new List<TextToken>())
			.ToList();
		foreach (var token in tokens)
		{
			var center = token.Bounds.X + (token.Bounds.Width / 2d);
			var index = 0;
			while (index < mergedSplits.Count && center > mergedSplits[index])
			{
				index++;
			}

			columns[index].Add(token);
		}

		var minColumnSize = Math.Max(3, (int)Math.Ceiling(tokens.Count * 0.08d));
		for (var i = columns.Count - 1; i >= 0; i--)
		{
			if (columns[i].Count >= minColumnSize || columns.Count <= 1)
			{
				continue;
			}

			if (i == 0)
			{
				columns[1].AddRange(columns[0]);
				columns.RemoveAt(0);
				continue;
			}

			if (i == columns.Count - 1)
			{
				columns[i - 1].AddRange(columns[i]);
				columns.RemoveAt(i);
				continue;
			}

			var leftWidth = Rectangle.Union(columns[i - 1].Select(t => t.Bounds)).Width;
			var rightWidth = Rectangle.Union(columns[i + 1].Select(t => t.Bounds)).Width;
			if (leftWidth <= rightWidth)
			{
				columns[i - 1].AddRange(columns[i]);
			}
			else
			{
				columns[i + 1].AddRange(columns[i]);
			}

			columns.RemoveAt(i);
		}

		var result = columns
			.Where(c => c.Count > 0)
			.Select(c => c.OrderByDescending(t => t.Bounds.Y).ThenBy(t => t.Bounds.X).ToList())
			.OrderBy(c => c.Min(t => t.Bounds.X))
			.ToList();

		return result.Count > 0 ? result : [tokens.ToList()];
	}

	private static List<List<TextLine>> SplitLinesIntoHorizontalBands(IReadOnlyList<TextLine> lines)
	{
		if (lines.Count == 0)
		{
			return [];
		}

		var ordered = lines.OrderByDescending(l => l.Bounds.Y).ToList();
		var medianHeight = ordered.Select(l => l.Bounds.Height).DefaultIfEmpty(12d).Median();
		var bandGapThreshold = Math.Max(8d, medianHeight * 2.4d);

		var bands = new List<List<TextLine>>();
		var current = new List<TextLine> { ordered[0] };
		for (var i = 1; i < ordered.Count; i++)
		{
			var previous = ordered[i - 1];
			var line = ordered[i];
			var gap = previous.Bounds.Bottom - line.Bounds.Y;
			if (gap > bandGapThreshold)
			{
				bands.Add(current);
				current = [];
			}

			current.Add(line);
		}

		bands.Add(current);
		return bands;
	}

	private static List<List<TextLine>> SplitLinesIntoVerticalZones(IReadOnlyList<TextLine> lines)
	{
		if (lines.Count == 0)
		{
			return [];
		}

		var ordered = lines.OrderByDescending(l => l.Bounds.Y).ToList();
		var medianHeight = ordered.Select(l => l.Bounds.Height).DefaultIfEmpty(12d).Median();
		var zoneGapThreshold = Math.Max(5d, medianHeight * 1.8d);

		var zones = new List<List<TextLine>>();
		var current = new List<TextLine> { ordered[0] };
		for (var i = 1; i < ordered.Count; i++)
		{
			var previous = ordered[i - 1];
			var line = ordered[i];
			var gap = previous.Bounds.Bottom - line.Bounds.Y;
			if (gap > zoneGapThreshold)
			{
				zones.Add(current);
				current = [];
			}

			current.Add(line);
		}

		zones.Add(current);
		return zones;
	}

	private static IReadOnlyList<TextLine> BuildLines(List<TextToken> tokens, int pageNumber, string zoneId)
	{
		if (tokens.Count == 0)
		{
			return [];
		}

		var heightRef = tokens.Select(t => t.Bounds.Height).DefaultIfEmpty(12d).Median();
		var threshold = Math.Max(2d, heightRef * 0.7d);
		var lineBuckets = new List<List<TextToken>>();
		var lineCenters = new List<double>();

		foreach (var token in tokens)
		{
			var centerY = token.Bounds.Y - (token.Bounds.Height / 2d);
			var attached = false;
			for (var i = 0; i < lineCenters.Count; i++)
			{
				if (Math.Abs(lineCenters[i] - centerY) <= threshold)
				{
					lineBuckets[i].Add(token);
					lineCenters[i] = (lineCenters[i] + centerY) / 2d;
					attached = true;
					break;
				}
			}

			if (!attached)
			{
				lineBuckets.Add([token]);
				lineCenters.Add(centerY);
			}
		}

		var sorted = lineBuckets
			.Select((bucket, idx) => new
			{
				Index = idx,
				Tokens = bucket.OrderBy(t => t.Bounds.X).ToList(),
				Top = bucket.Max(t => t.Bounds.Y)
			})
			.OrderByDescending(x => x.Top)
			.ToList();

		var lines = new List<TextLine>(sorted.Count);
		for (var i = 0; i < sorted.Count; i++)
		{
			var segments = SplitLineByLargeGaps(sorted[i].Tokens);
			for (var s = 0; s < segments.Count; s++)
			{
				var lineTokens = segments[s];
				var bounds = Rectangle.Union(lineTokens.Select(t => t.Bounds));
				var text = MergeTokenText(lineTokens);
				lines.Add(new TextLine(
					$"{zoneId}-l{i + 1}-s{s + 1}",
					pageNumber,
					zoneId,
					bounds,
					lineTokens,
					text));
			}
		}

		return lines;
	}

	private static List<List<TextToken>> SplitLineByLargeGaps(List<TextToken> tokens)
	{
		if (tokens.Count < 2)
		{
			return [tokens];
		}

		var positiveGaps = new List<double>();
		for (var i = 1; i < tokens.Count; i++)
		{
			var gap = tokens[i].Bounds.X - tokens[i - 1].Bounds.Right;
			if (gap > 0d)
			{
				positiveGaps.Add(gap);
			}
		}

		if (positiveGaps.Count == 0)
		{
			return [tokens];
		}

		var medianGap = positiveGaps.Median();
		var tokenHeight = tokens.Select(t => t.Bounds.Height).DefaultIfEmpty(10d).Median();
		var splitThreshold = Math.Max(24d, Math.Max(tokenHeight * 3.5d, medianGap * 4.5d));

		var segments = new List<List<TextToken>>();
		var current = new List<TextToken> { tokens[0] };
		for (var i = 1; i < tokens.Count; i++)
		{
			var gap = tokens[i].Bounds.X - tokens[i - 1].Bounds.Right;
			if (gap >= splitThreshold)
			{
				segments.Add(current);
				current = [];
			}

			current.Add(tokens[i]);
		}

		segments.Add(current);
		return segments;
	}

	private static string MergeTokenText(IReadOnlyList<TextToken> tokens)
	{
		if (tokens.Count == 0)
		{
			return string.Empty;
		}

		if (tokens.Count == 1)
		{
			return tokens[0].Text;
		}

		var sb = new StringBuilder(tokens[0].Text);
		for (var i = 1; i < tokens.Count; i++)
		{
			var prev = tokens[i - 1];
			var current = tokens[i];
			var gap = current.Bounds.X - prev.Bounds.Right;
			var addSpace = ShouldInsertSpaceBetweenTokens(prev.Text, current.Text, gap, prev.Bounds.Height);
			if (addSpace)
			{
				sb.Append(' ');
			}

			sb.Append(current.Text);
		}

		return sb.ToString().Trim();
	}

	private static bool ShouldInsertSpaceBetweenTokens(string previousText, string currentText, double gap, double previousHeight)
	{
		if (string.IsNullOrEmpty(previousText) || string.IsNullOrEmpty(currentText))
		{
			return false;
		}

		var currentFirst = currentText[0];
		if (",.;:!?)]}".Contains(currentFirst))
		{
			return false;
		}

		var previousLast = previousText[^1];
		if (previousLast is '\'' or '’')
		{
			return false;
		}

		if ("([{\"'".Contains(previousLast))
		{
			return false;
		}

		if (gap <= 0.2d)
		{
			return false;
		}

		return gap >= Math.Max(0.9d, previousHeight * 0.12d);
	}

	private static IReadOnlyList<TableBlock> DetectTables(int pageNumber, IReadOnlyList<VerticalZone> zones)
	{
		var tables = new List<TableBlock>();

		foreach (var zone in zones)
		{
			var lines = zone.Lines.OrderByDescending(l => l.Bounds.Y).ToList();
			if (lines.Count < 2)
			{
				continue;
			}

			var rowCandidates = lines
				.Select(l => new { Line = l, Cells = SplitIntoCells(l) })
				.ToList();

			var currentRows = new List<(TextLine Line, List<TextLine> Cells)>();
			for (var i = 0; i < rowCandidates.Count; i++)
			{
				var c = rowCandidates[i];
				if (c.Cells.Count < 2)
				{
					FlushTableIfNeeded(currentRows, tables, pageNumber, zone.Id);
					currentRows.Clear();
					continue;
				}

				if (currentRows.Count > 0)
				{
					var prev = currentRows[^1].Line;
					var gap = prev.Bounds.Bottom - c.Line.Bounds.Y;
					var avgHeight = (prev.Bounds.Height + c.Line.Bounds.Height) / 2d;
					if (gap > avgHeight * 1.8d)
					{
						FlushTableIfNeeded(currentRows, tables, pageNumber, zone.Id);
						currentRows.Clear();
					}
				}

				currentRows.Add((c.Line, c.Cells));
			}

			FlushTableIfNeeded(currentRows, tables, pageNumber, zone.Id);
		}

		return tables;
	}

	private static List<TextLine> SplitIntoCells(TextLine line)
	{
		if (line.Tokens.Count < 2)
		{
			return [];
		}

		var tokens = line.Tokens.OrderBy(t => t.Bounds.X).ToList();
		var medianWidth = tokens.Select(t => t.Bounds.Width).DefaultIfEmpty(10d).Median();
		var gapThreshold = Math.Max(8d, medianWidth * 2.2d);

		var cells = new List<List<TextToken>>();
		var current = new List<TextToken> { tokens[0] };
		for (var i = 1; i < tokens.Count; i++)
		{
			var gap = tokens[i].Bounds.X - tokens[i - 1].Bounds.Right;
			if (gap >= gapThreshold)
			{
				cells.Add(current);
				current = [];
			}

			current.Add(tokens[i]);
		}

		cells.Add(current);
		if (cells.Count < 2)
		{
			return [];
		}

		return cells
			.Select((group, i) => new TextLine(
				$"{line.Id}-c{i + 1}",
				line.PageNumber,
				line.ZoneId,
				Rectangle.Union(group.Select(t => t.Bounds)),
				group,
				MergeTokenText(group)))
			.ToList();
	}

	private static void FlushTableIfNeeded(
		List<(TextLine Line, List<TextLine> Cells)> rows,
		List<TableBlock> tables,
		int pageNumber,
		string zoneId)
	{
		if (rows.Count < 2)
		{
			return;
		}

		var maxColumns = rows.Max(r => r.Cells.Count);
		if (maxColumns < 2)
		{
			return;
		}

		var tableRows = new List<TableRow>(rows.Count);
		for (var r = 0; r < rows.Count; r++)
		{
			var ordered = rows[r].Cells.OrderBy(c => c.Bounds.X).ToList();
			var cells = ordered
				.Select((c, col) => new TableCell(r, col, c.Bounds, c.Text, [c]))
				.ToList();
			tableRows.Add(new TableRow(r, cells));
		}

		var tableBounds = Rectangle.Union(rows.Select(x => x.Line.Bounds));
		tables.Add(new TableBlock($"{zoneId}-tbl{tables.Count + 1}", pageNumber, tableBounds, tableRows));
	}

	private static IReadOnlyList<ParagraphAnalysis> BuildParagraphs(VerticalZone zone, int pageNumber)
	{
		var lines = zone.Lines.OrderByDescending(x => x.Bounds.Y).ToList();
		if (lines.Count == 0)
		{
			return [];
		}

		var medianHeight = lines.Select(l => l.Bounds.Height).DefaultIfEmpty(12d).Median();
		var paragraphGapThreshold = Math.Max(6d, medianHeight * 1.9d);
		var result = new List<SemanticParagraph>();
		var current = new List<TextLine>();

		for (var i = 0; i < lines.Count; i++)
		{
			var line = lines[i];
			if (current.Count == 0)
			{
				current.Add(line);
				continue;
			}

			var prev = current[^1];
			var verticalGap = prev.Bounds.Bottom - line.Bounds.Y;
			var indentShift = Math.Abs(line.Bounds.X - prev.Bounds.X);
			var prevStyle = GetDominantLineStyle(prev);
			var currStyle = GetDominantLineStyle(line);
			var fontChanged = !HasSameFont(prevStyle.FontKey, currStyle.FontKey);
			var sizeDelta = Math.Abs(prevStyle.FontSize - currStyle.FontSize);
			var strongSizeChanged = sizeDelta > Math.Max(1.6f, prevStyle.FontSize * 0.25f);
			var isTitleLike = IsTitleLikeLine(prev.Text) || IsTitleLikeLine(line.Text);
			var prevStandalone = IsLikelyStandaloneLine(prev.Text);
			var currStandalone = IsLikelyStandaloneLine(line.Text);
			var prevShort = WordCount(prev.Text) <= 4;
			var currShort = WordCount(line.Text) <= 4;
			var breakParagraph =
				verticalGap > paragraphGapThreshold
				|| indentShift > medianHeight * 1.8d
				|| ((prevStandalone || currStandalone) && verticalGap > medianHeight * 0.35d)
				|| ((prevShort || currShort) && fontChanged && verticalGap > medianHeight * 0.5d)
				|| (isTitleLike && fontChanged && strongSizeChanged && verticalGap > medianHeight * 0.75d);
			if (breakParagraph)
			{
				result.Add(CreateParagraph(current, zone, pageNumber, result.Count + 1));
				current.Clear();
			}

			current.Add(line);
		}

		if (current.Count > 0)
		{
			result.Add(CreateParagraph(current, zone, pageNumber, result.Count + 1));
		}

		var lineById = lines.ToDictionary(l => l.Id, l => l, StringComparer.Ordinal);
		var analyses = new List<ParagraphAnalysis>(result.Count);
		for (var i = 0; i < result.Count; i++)
		{
			var paragraph = result[i];
			var paragraphLines = paragraph.LineIds
				.Select(id => lineById.TryGetValue(id, out var line) ? line : null)
				.Where(line => line is not null)
				.Select(line => line!)
				.ToList();

			analyses.Add(new ParagraphAnalysis(paragraph, GetParagraphStyle(paragraphLines)));
		}

		return analyses;
	}

	private static Dictionary<string, int> AssignHeadingLevels(
		IReadOnlyList<SemanticParagraph> paragraphs,
		IReadOnlyDictionary<string, ParagraphAnalysis> analysisByParagraphId)
	{
		var headingLevels = new Dictionary<string, int>(StringComparer.Ordinal);
		var styleToLevel = new Dictionary<HeadingStyleKey, int>();

		var baselineSize = analysisByParagraphId.Values
			.Select(x => (double)x.Style.FontSize)
			.Where(size => size > 0d)
			.DefaultIfEmpty(0d)
			.Median();

		if (baselineSize <= 0d)
		{
			return headingLevels;
		}

		var nextLevel = 1;
		var orderedParagraphs = paragraphs
			.OrderBy(p => p.PageNumber)
			.ThenByDescending(p => p.Bounds.Y)
			.ThenBy(p => p.Bounds.X)
			.ToList();

		foreach (var paragraph in orderedParagraphs)
		{
			if (!analysisByParagraphId.TryGetValue(paragraph.Id, out var analysis))
			{
				continue;
			}

			if (!IsHeadingCandidate(paragraph.Text, analysis.Style.FontSize, baselineSize, analysis.Style.LineCount))
			{
				continue;
			}

			var styleKey = ToHeadingStyleKey(analysis.Style);
			if (!styleToLevel.TryGetValue(styleKey, out var level))
			{
				level = nextLevel;
				styleToLevel[styleKey] = level;
				nextLevel = Math.Min(6, nextLevel + 1);
			}

			headingLevels[paragraph.Id] = level;
		}

		if (headingLevels.Values.Count(level => level == 2) == 1)
		{
			foreach (var paragraphId in headingLevels.Keys.ToList())
			{
				headingLevels[paragraphId] = Math.Max(1, headingLevels[paragraphId] - 1);
			}
		}

		return headingLevels;
	}

	private static int WordCount(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return 0;
		}

		return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
	}

	private static bool IsLikelyStandaloneLine(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var trimmed = text.Trim();
		if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			|| trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (trimmed.StartsWith("● ", StringComparison.Ordinal)
			|| trimmed.StartsWith("- ", StringComparison.Ordinal)
			|| trimmed.StartsWith("• ", StringComparison.Ordinal))
		{
			return true;
		}

		if (trimmed.All(c => char.IsDigit(c) || c is '/' or '-' or ' ')
			&& trimmed.Any(char.IsDigit))
		{
			return true;
		}

		if (trimmed.All(c => char.IsDigit(c) || char.IsWhiteSpace(c)))
		{
			return true;
		}

		if (IsTitleLikeLine(trimmed) && WordCount(trimmed) <= 5)
		{
			return true;
		}

		return false;
	}

	private static bool IsTitleLikeLine(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var trimmed = text.Trim();
		if (trimmed.Length > 64)
		{
			return false;
		}

		var letters = trimmed.Where(char.IsLetter).ToArray();
		if (letters.Length == 0)
		{
			return false;
		}

		var upperRatio = letters.Count(char.IsUpper) / (double)letters.Length;
		return upperRatio > 0.75d;
	}

	private static (string FontKey, float FontSize, FontWeightClass WeightClass) GetDominantLineStyle(TextLine line)
	{
		if (line.Tokens.Count == 0)
		{
			return (string.Empty, 0f, FontWeightClass.Normal);
		}

		var best = line.Tokens
			.GroupBy(t => new
			{
				Name = NormalizeFontFamily(t.FontName),
				Size = (float)Math.Round(t.FontSize, 1),
				Weight = DetectFontWeightClass(t.FontName)
			})
			.Select(g => new
			{
				g.Key.Name,
				g.Key.Size,
				g.Key.Weight,
				ScoreWeight = g.Sum(t => Math.Max(1, t.Text.Length))
			})
			.OrderByDescending(x => x.ScoreWeight)
			.First();

		return (best.Name, best.Size, best.Weight);
	}

	private static bool HasSameFont(string? left, string? right)
	{
		var a = NormalizeFontFamily(left);
		var b = NormalizeFontFamily(right);
		return a == b;
	}

	private static string NormalizeFontName(string? fontName)
	{
		if (string.IsNullOrWhiteSpace(fontName))
		{
			return string.Empty;
		}

		var name = fontName;
		var plusIndex = name.IndexOf('+');
		if (plusIndex >= 0 && plusIndex < name.Length - 1)
		{
			name = name[(plusIndex + 1)..];
		}

		var cleaned = new string(name.Where(char.IsLetterOrDigit).ToArray());
		return cleaned.ToLowerInvariant();
	}

	private static string NormalizeFontFamily(string? fontName)
	{
		var normalized = NormalizeFontName(fontName);
		if (string.IsNullOrEmpty(normalized))
		{
			return string.Empty;
		}

		var family = normalized
			.Replace("extrabold", string.Empty, StringComparison.Ordinal)
			.Replace("ultrabold", string.Empty, StringComparison.Ordinal)
			.Replace("semibold", string.Empty, StringComparison.Ordinal)
			.Replace("demibold", string.Empty, StringComparison.Ordinal)
			.Replace("bold", string.Empty, StringComparison.Ordinal)
			.Replace("black", string.Empty, StringComparison.Ordinal)
			.Replace("heavy", string.Empty, StringComparison.Ordinal)
			.Replace("medium", string.Empty, StringComparison.Ordinal)
			.Replace("regular", string.Empty, StringComparison.Ordinal)
			.Replace("roman", string.Empty, StringComparison.Ordinal)
			.Replace("book", string.Empty, StringComparison.Ordinal);

		return string.IsNullOrEmpty(family) ? normalized : family;
	}

	private static FontWeightClass DetectFontWeightClass(string? fontName)
	{
		var normalized = NormalizeFontName(fontName);
		if (string.IsNullOrEmpty(normalized))
		{
			return FontWeightClass.Normal;
		}

		if (normalized.Contains("bold", StringComparison.Ordinal)
			|| normalized.Contains("black", StringComparison.Ordinal)
			|| normalized.Contains("heavy", StringComparison.Ordinal))
		{
			return FontWeightClass.Bold;
		}

		if (normalized.Contains("semi", StringComparison.Ordinal)
			|| normalized.Contains("demi", StringComparison.Ordinal)
			|| normalized.Contains("medium", StringComparison.Ordinal))
		{
			return FontWeightClass.SemiBold;
		}

		return FontWeightClass.Normal;
	}

	private static ParagraphStyle GetParagraphStyle(IReadOnlyList<TextLine> lines)
	{
		if (lines.Count == 0)
		{
			return new ParagraphStyle(string.Empty, 0f, FontWeightClass.Normal, 0);
		}

		double weightedSize = 0d;
		double weightSum = 0d;
		var fontWeight = new Dictionary<(string FontKey, FontWeightClass WeightClass), int>();

		foreach (var line in lines)
		{
			var style = GetDominantLineStyle(line);
			var weight = Math.Max(1, line.Text.Length);
			if (style.FontSize > 0f)
			{
				weightedSize += style.FontSize * weight;
				weightSum += weight;
			}

			var key = (style.FontKey, style.WeightClass);
			fontWeight[key] = fontWeight.TryGetValue(key, out var current) ? current + weight : weight;
		}

		var dominant = fontWeight.Count == 0
			? (FontKey: string.Empty, WeightClass: FontWeightClass.Normal)
			: fontWeight.OrderByDescending(x => x.Value).First().Key;

		var fontSize = weightSum <= 0d ? 0f : (float)(weightedSize / weightSum);
		return new ParagraphStyle(dominant.FontKey, fontSize, dominant.WeightClass, lines.Count);
	}

	private static bool IsHeadingCandidate(string text, float paragraphFontSize, double baselineSize, int lineCount)
	{
		if (paragraphFontSize <= 0f || baselineSize <= 0d)
		{
			return false;
		}

		var trimmed = text.Trim();
		if (string.IsNullOrWhiteSpace(trimmed))
		{
			return false;
		}

		var words = WordCount(trimmed);
		if (words is 0 || words > 16 || lineCount > 4)
		{
			return false;
		}

		if (trimmed.EndsWith(".", StringComparison.Ordinal))
		{
			return false;
		}

		if (IsLikelyStandaloneLine(trimmed) && !IsTitleLikeLine(trimmed))
		{
			return false;
		}

		var ratio = paragraphFontSize / baselineSize;
		return ratio >= 1.08d || IsTitleLikeLine(trimmed);
	}

	private static HeadingStyleKey ToHeadingStyleKey(ParagraphStyle style)
	{
		var sizeBucket = style.FontSize <= 0f
			? 0
			: (int)Math.Round(style.FontSize * 4f, MidpointRounding.AwayFromZero);

		return new HeadingStyleKey(style.FontKey, sizeBucket, style.WeightClass);
	}

	private static SemanticParagraph CreateParagraph(List<TextLine> lines, VerticalZone zone, int pageNumber, int index)
	{
		var ordered = lines.OrderByDescending(l => l.Bounds.Y).ToList();
		var text = string.Join(" ", ordered.Select(l => l.Text.Trim()))
			.Trim();
		return new SemanticParagraph(
			$"{zone.Id}-p{index}",
			pageNumber,
			zone.Id,
			Rectangle.Union(ordered.Select(x => x.Bounds)),
			ordered.Select(x => x.Id).ToList(),
			text,
			null);
	}

	private static double Median(this IEnumerable<double> source)
	{
		var values = source.OrderBy(x => x).ToArray();
		if (values.Length == 0)
		{
			return 0d;
		}

		var mid = values.Length / 2;
		return values.Length % 2 == 0 ? (values[mid - 1] + values[mid]) / 2d : values[mid];
	}

	private readonly record struct ParagraphAnalysis(SemanticParagraph Paragraph, ParagraphStyle Style);

	private readonly record struct ParagraphStyle(string FontKey, float FontSize, FontWeightClass WeightClass, int LineCount);

	private readonly record struct HeadingStyleKey(string FontKey, int SizeBucket, FontWeightClass WeightClass);

	private enum FontWeightClass
	{
		Normal,
		SemiBold,
		Bold
	}
}
