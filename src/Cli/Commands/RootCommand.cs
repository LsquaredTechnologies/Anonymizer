using System.CommandLine;

using Microsoft.Extensions.Hosting;

namespace Anonymizer.Commands;

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
        Add(new RunCommand(builder));
        Add(new ConfigCommand(builder));
        Add(new StartCommand(builder));

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is "setup")
            {
                Add(new InstallCommand(builder));
                Add(new UninstallCommand(builder));
                Add(new UpdateCommand(builder));
            }
        }

        TreatUnmatchedTokensAsErrors = true;
    }
}
