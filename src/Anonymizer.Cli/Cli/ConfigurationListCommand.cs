using System.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli;

internal sealed class ConfigurationListCommand : Command
{
    private const string HelpDesc = """
        Prints the configuration.
        """;

    public ConfigurationListCommand(IHostBuilder builder) : base("list", HelpDesc) =>
        SetAction((parseResult) =>
        {
            var app = builder.Build();
            var configuration = app.Services.GetRequiredService<IConfiguration>();
            var files = configuration.GetValue<string>("files") ?? string.Empty;
            var models = configuration.GetValue<string>("models") ?? string.Empty;

            Console.WriteLine($"files={files}");
            Console.WriteLine($"models={models}");
        });
}
