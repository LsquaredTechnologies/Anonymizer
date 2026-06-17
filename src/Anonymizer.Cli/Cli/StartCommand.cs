using System.CommandLine;

using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli;

internal sealed class StartCommand : Command
{
    private const string HelpDesc = """
        The "start" command starts the application in the background.
        """;

    public StartCommand(IHostBuilder builder) : base("start", HelpDesc)
    {
        Hidden = true;
        SetAction(async (_) =>
        {
            var app = builder.Build();
            await app.RunAsync();
        });
    }
}
