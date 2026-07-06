using Anonymizer.Lifetime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using static Anonymizer.Internals.ConsoleA;

namespace Anonymizer.Commands;

internal sealed partial class StartCommand
{
    public static async Task ExecuteAsync(IHostBuilder builder)
    {
        AttachToConsole();
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
    }
}
