using System.CommandLine;

namespace Anonymizer.Cli;

internal sealed class VerboseOption : Option<bool>
{
    public VerboseOption() : base("--verbose", "-v", "/v") =>
        Description = "Show detected PII and audit";
}
