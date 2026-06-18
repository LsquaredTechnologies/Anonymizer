using System.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli;

internal sealed class ConfigurationGetCommand : Command
{
    private const string HelpDesc = """
        Get the configuration value from the key name.
        """;

    private readonly Argument<string> _keyArgument = new("Key")
    {
        Arity = ArgumentArity.ExactlyOne,
        Description = "The key name of the value to retrieve",
    };

    public ConfigurationGetCommand(IHostBuilder builder) : base("get", HelpDesc)
    {
        Add(_keyArgument);
        SetAction((parseResult) =>
        {
            var app = builder.Build();
            var configuration = app.Services.GetRequiredService<IConfiguration>();

            var keyName = parseResult.GetRequiredValue(_keyArgument);
            var value = configuration.GetValue<string>(keyName);
            Console.WriteLine(value);
        });
    }
}
