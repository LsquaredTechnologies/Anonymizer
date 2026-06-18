using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli.Commands;

internal sealed class ConfigSetCommand : Command
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

    public ConfigSetCommand(IHostBuilder builder) : base("set", HelpDesc)
    {
        Add(_keyArgument);
        Add(_valueArgument);
        SetAction((parseResult) =>
        {
            var app = builder.Build();
            var configuration = app.Services.GetRequiredService<IConfiguration>();

            var key = parseResult.GetRequiredValue(_keyArgument);
            var value = Environment.ExpandEnvironmentVariables(parseResult.GetRequiredValue(_valueArgument));

            if (!Application.Config.ValidConfigKey.Contains(key)) return;

            JsonObject o = new()
            {
                { key, value }
            };
            using var stream = File.Open(Application.AppSettings.Path, FileMode.Create, FileAccess.Write);
            JsonSerializer.Serialize(stream, o, JsonOptions);
        });
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };
}
