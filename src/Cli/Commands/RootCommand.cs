using System.CommandLine;
using System.ComponentModel;

using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli.Commands;

internal sealed class RootCommand : System.CommandLine.RootCommand
{
    private const string HelpDesc = """
        Anonymize PDF files
        """;

    public RootCommand(IHostBuilder builder) : base(HelpDesc)
    {
        Add(new DiagramDirective());
        Add(new EnvironmentVariablesDirective());

        Add(new DownloadCommand(builder));

        TreatUnmatchedTokensAsErrors = true;
    }
}
