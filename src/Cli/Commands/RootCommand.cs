using System.CommandLine;

namespace Anonymizer.Cli.Commands;

internal sealed class RootCommand : System.CommandLine.RootCommand
{
    private const string HelpDesc = """
        Anonymize PDF files
        """;

    public RootCommand() : base(HelpDesc)
    {
        Add(new DiagramDirective());
        Add(new EnvironmentVariablesDirective());

        TreatUnmatchedTokensAsErrors = true;
    }
}
