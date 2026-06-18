using System.CommandLine;

using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli;

internal sealed class RootCommand : System.CommandLine.RootCommand
{
    private const string HelpDesc = """
        Anonymize PDF files
        """;

    public RootCommand(IHostBuilder builder) : base(HelpDesc)
    {
        Add(new DiagramDirective());
        Add(new EnvironmentVariablesDirective());

        Add(new RunCommand(builder));
        Add(new DownloadCommand(builder));

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Add(new InstallCommand());
            Add(new UninstallCommand());
            Add(new UpdateCommand());
        }

        TreatUnmatchedTokensAsErrors = true;
    }
}
