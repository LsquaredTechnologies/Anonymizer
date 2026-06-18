using System.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli.Commands;

internal sealed class ConfigGetCommand : Command
{
    private const string HelpDesc = """
        Get the configuration value from the key name.
        """;

    private readonly Argument<string> _keyArgument = new("Key")
    {
        Arity = ArgumentArity.ExactlyOne,
        Description = "The key name of the value to retrieve",
    };

    public ConfigGetCommand(IHostBuilder builder) : base("get", HelpDesc)
    {
        Add(_keyArgument);
        SetAction((parseResult) =>
        {
            var app = builder.Build();
            var configuration = app.Services.GetRequiredService<IConfiguration>();

            var key = parseResult.GetRequiredValue(_keyArgument).Replace('.', ':');
            if (!Application.Config.ValidConfigKey.Contains(key)) return;

            var value = configuration.GetValue<string>(key);
            Console.WriteLine(value);
        });
    }
}
