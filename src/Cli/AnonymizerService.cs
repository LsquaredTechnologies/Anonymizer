using System.Text;
using System.Text.Json;
using Anonymizer.Extractor.Documents;
using Anonymizer.Extractor.Documents.PDF;
using Anonymizer.Extractor.PII;

using Microsoft.Extensions.Logging;

namespace Anonymizer.Cli;

internal sealed partial class AnonymizerService(ILogger<AnonymizerService> logger)
{
    public async Task AnonymizeFilesAsync(IEnumerable<FileSystemInfo> filesOrDirs, CancellationToken cancellationToken = default)
    {
        List<Task> tasks = [];
        foreach (var fileOrDir in filesOrDirs)
            tasks.Add(AnonymizeFilesAsync(fileOrDir, cancellationToken));

        await Task.WhenAll(tasks);
    }

    public async Task AnonymizeFilesAsync(FileSystemInfo fileOrDir, CancellationToken cancellationToken = default)
    {
        if (fileOrDir is DirectoryInfo dir)
        {
            await AnonymizeFilesAsync(dir.GetFiles("*.pdf", SearchOption.AllDirectories), cancellationToken);
        }
        else if (fileOrDir is FileInfo file)
        {
            await AnonymizeFileAsync(file, cancellationToken);
        }
    }

    public async Task AnonymizeFileAsync(FileInfo file, CancellationToken cancellationToken = default)
    {
        // skip if already anonymized
        if (file.Name.EndsWith(".anon.pdf")) return;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            await RunPipelineAsync(file, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task RunPipelineAsync(FileInfo file, CancellationToken cancellationToken)
    {
        Document document = PDFDocumentTextExtractor.Extract(file.FullName);
        string markdown = BuildMarkdown(document);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            LogNoTextExtracted(file.Name);
            return;
        }

        List<Entity> entities = DetectEntities(markdown);
        string anonymizedMarkdown = ApplyRedactions(markdown, entities);

        OutputPaths output = OutputPaths.From(file);
        await File.WriteAllTextAsync(output.MarkdownPath, markdown, cancellationToken);
        await File.WriteAllTextAsync(output.AnonymizedMarkdownPath, anonymizedMarkdown, cancellationToken);

        string nerJson = JsonSerializer.Serialize(entities, Entity.JsonContext);
        await File.WriteAllTextAsync(output.NerJsonPath, nerJson, cancellationToken);

        PdfTextWriter.Write(output.AnonymizedPdfPath, anonymizedMarkdown);
        LogProcessTerminated(file.Name, entities.Count, output.AnonymizedPdfPath);
    }

    private List<Entity> DetectEntities(string text)
    {
        List<Entity> entities = [];

        try
        {
            using OnnxPiiDetector detector = new(ResolveModelsDirectory());
            entities.AddRange(detector.AnalyzeText(text));
        }
        catch (Exception ex)
        {
            LogOnnxDetectionError(ex);
        }

        entities.AddRange(_regexDetector.AnalyzeText(text));

        return NormalizeEntities(entities, text.Length);
    }

    private static DirectoryInfo ResolveModelsDirectory()
    {
        if (HasPiiModel(Application.Models.Dir))
        {
            return Application.Models.Dir;
        }

        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null)
        {
            DirectoryInfo candidate = new(Path.Combine(current.FullName, "models"));
            if (HasPiiModel(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return Application.Models.Dir;
    }

    private static bool HasPiiModel(DirectoryInfo modelsDir) =>
        File.Exists(Path.Combine(modelsDir.FullName, "pii", "tokenizer.json"))
        && File.Exists(Path.Combine(modelsDir.FullName, "pii", "model.onnx"));

    private static string BuildMarkdown(Document document)
    {
        var paragraphs = document.SemanticTree.Paragraphs
            .OrderBy(p => p.PageNumber)
            .ThenByDescending(p => p.Bounds.Y)
            .ThenBy(p => p.Bounds.X)
            .Select(p =>
            {
                string text = p.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return string.Empty;
                }

                if (p.HeadingLevel is int level && level is >= 1 and <= 6)
                {
                    return new string('#', level) + " " + text;
                }

                return text;
            })
            .Where(text => !string.IsNullOrWhiteSpace(text));

        return string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
    }

    private static string ApplyRedactions(string text, IReadOnlyList<Entity> entities)
    {
        if (entities.Count is 0) return text;

        StringBuilder builder = new(text);
        foreach (Entity entity in entities.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
        {
            int start = entity.Start;
            int end = Math.Min(entity.End, builder.Length);
            if (start < 0 || end <= start || start >= builder.Length) continue;

            builder.Remove(start, end - start);
            builder.Insert(start, GetPlaceholder(entity.Label));
        }

        return builder.ToString();
    }

    private static List<Entity> NormalizeEntities(IEnumerable<Entity> entities, int textLength)
    {
        List<Entity> ordered = entities
            .Where(e => e.Start >= 0 && e.End > e.Start && e.Start < textLength)
            .Select(e => new Entity
            {
                Label = e.Label,
                Start = e.Start,
                End = Math.Min(textLength, e.End),
                Text = e.Text,
                Score = e.Score,
            })
            .OrderBy(e => e.Start)
            .ThenByDescending(e => e.End)
            .ThenByDescending(e => e.Score)
            .ToList();

        if (ordered.Count < 2) return ordered;

        List<Entity> merged = [ordered[0]];
        for (int i = 1; i < ordered.Count; i++)
        {
            Entity current = ordered[i];
            Entity previous = merged[^1];
            if (current.Start >= previous.End)
            {
                merged.Add(current);
                continue;
            }

            int previousLength = previous.End - previous.Start;
            int currentLength = current.End - current.Start;
            bool shouldReplace = currentLength > previousLength || (currentLength == previousLength && current.Score > previous.Score);
            if (shouldReplace)
            {
                merged[^1] = current;
            }
        }

        return merged;
    }

    private static string GetPlaceholder(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "[DONNEE_SENSIBLE]";
        string normalized = label.Trim().ToUpperInvariant();

        return normalized switch
        {
            "PER" or "PERSON" or "NOM_PERSONNE" or "PRENOM_PERSONNE" => "[NOM_PERSONNE]",
            "ORG" or "NOM_SOCIETE" => "[NOM_SOCIETE]",
            "LOC" or "GPE" or "LIEU" => "[LIEU]",
            "EMAIL" => "[EMAIL]",
            "PHONE" => "[TELEPHONE]",
            "URL" => "[URL]",
            "DATE" or "BIRTHDATE" => "[DATE]",
            _ => "[DONNEE_SENSIBLE]",
        };
    }

#pragma warning disable CA1822

    [LoggerMessage(LogLevel.Information, "File {FileName} processed ({EntityCount} entities). Output: {OutputPdf}")]
    private partial void LogProcessTerminated(string fileName, int entityCount, string outputPdf);

    [LoggerMessage(LogLevel.Warning, "No text extracted from {FileName}. Skipping anonymization output.")]
    private partial void LogNoTextExtracted(string fileName);

    [LoggerMessage(LogLevel.Warning, "ONNX detector failed, fallback to regex only.")]
    private partial void LogOnnxDetectionError(Exception ex);

#pragma warning restore CA1822

    // Maximum 4 files processed at same time!
    private readonly SemaphoreSlim _semaphore = new(4);
    private readonly RegexPiiDetector _regexDetector = new();
    private readonly ILogger _logger = logger;

    private readonly record struct OutputPaths(
        string MarkdownPath,
        string AnonymizedMarkdownPath,
        string NerJsonPath,
        string AnonymizedPdfPath)
    {
        public static OutputPaths From(FileInfo file)
        {
            string basePath = Path.Combine(file.DirectoryName!, Path.GetFileNameWithoutExtension(file.Name));
            return new(
                MarkdownPath: basePath + ".md",
                AnonymizedMarkdownPath: basePath + ".anonymized.md",
                NerJsonPath: basePath + ".ner.json",
                AnonymizedPdfPath: basePath + ".anon.pdf");
        }
    }
}
