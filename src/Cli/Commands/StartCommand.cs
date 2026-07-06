using System.CommandLine;

using Anonymizer.Lifetime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Commands;

internal sealed class StartCommand : Command
{
    private const string HelpDesc = """
        Starts the application in the background.
        """;

    public StartCommand(IHostBuilder builder) : base("start", HelpDesc)
    {
        Hidden = true;
        SetAction(async (_) =>
        {
            using var alreadyRunning = SingleInstance.TryAcquire("AppStart");
            if (!alreadyRunning.IsAcquired)
            {
                Console.Error.WriteLine("Anonymizer is already running.");
                return;
            }

            builder.ConfigureServices((context, services) =>
            {
                services.AddHostedService<FilesWatcher>();
                services.Configure<FilesWatcherOptions>(context.Configuration);
            });
            await builder.Build().RunAsync();
        });

    }
}
