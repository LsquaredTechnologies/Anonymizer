using System.CommandLine;

using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli.Commands;

internal sealed class ConfigCommand : Command
{
    private const string HelpDesc = """
        Manage the configuration of the application.
        """;

    public ConfigCommand(IHostBuilder builder) : base("config", HelpDesc)
    {
        Add(new ConfigGetCommand(builder));
        Add(new ConfigSetCommand(builder));
    }
}
