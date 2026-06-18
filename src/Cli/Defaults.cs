using System.Text.Json;

namespace Anonymizer.Cli;

internal static class Defaults
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };
}
