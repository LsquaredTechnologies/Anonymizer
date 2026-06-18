using System.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli;

internal sealed class ConfigurationCommand : Command
{
    private const string HelpDesc = """
        Manage the configuration of the application.
        """;

    public ConfigurationCommand(IHostBuilder builder) : base("config", HelpDesc)
    {
        Add(new ConfigurationGetCommand(builder));
        Add(new ConfigurationSetCommand(builder));
        Add(new ConfigurationListCommand(builder));
    }
}
