using System.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Anonymizer.Cli;

internal sealed class ConfigurationSetCommand : Command
{
    private const string HelpDesc = """
        Set the configuration value to the key name.
        """;

    private readonly Argument<string> _keyArgument = new("Key")
    {
        Arity = ArgumentArity.ExactlyOne,
        Description = "The key name of the value to set",
    };

    private readonly Argument<string> _valueArgument = new("Value")
    {
        Arity = ArgumentArity.ExactlyOne,
        Description = "The value to set",
    };

    public ConfigurationSetCommand(IHostBuilder builder) : base("set", HelpDesc)
    {
        Add(_keyArgument);
        Add(_valueArgument);
        SetAction((parseResult) =>
        {
            var app = builder.Build();
            var configuration = app.Services.GetRequiredService<IConfiguration>();

            var keyName = parseResult.GetRequiredValue(_keyArgument);
            var value = parseResult.GetRequiredValue(_valueArgument);

            var files = keyName is "files" ? value : configuration.GetValue<string>("files") ?? string.Empty;
            var models = keyName is "models" ? value : configuration.GetValue<string>("models") ?? string.Empty;
            PathProvider.SaveConfiguration(files, models);
        });
    }
}
