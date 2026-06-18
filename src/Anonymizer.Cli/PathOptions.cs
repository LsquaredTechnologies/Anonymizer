namespace Anonymizer;

internal sealed class PathOptions
{
    public string Models { get; init; } = "./models";
    public required string Files { get; init; }
}
