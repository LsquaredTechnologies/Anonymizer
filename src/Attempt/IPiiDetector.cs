namespace Anonymizer.Extractor.PII;

public interface IPiiDetector
{
    IReadOnlyList<Entity> AnalyzeText(string text);
}
