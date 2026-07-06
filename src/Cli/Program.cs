using Anonymizer.Cli;
using Anonymizer.Cli.Commands;
using Anonymizer.Cli.Downloaders;
using Anonymizer.Cli.Lifetime;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

using static Anonymizer.Cli.Internals.ConsoleA;

AttachToConsole();
Console.OutputEncoding = Console.InputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateDefaultBuilder();
builder.ConfigureServices((context, services) =>
{
    services.AddHttpClient<ModelDownloader>();
    services.AddSingleton<AnonymizerService>();
    services.AddSingleton<ProcessManager>();

    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        services.AddSingleton<SetupLifecycleService>();

    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is "setup")
    {
        if (OperatingSystem.IsWindows())
            services.AddSingleton<IAutostartManager, WindowsAutostart>();
        else if (OperatingSystem.IsLinux())
            services.AddSingleton<IAutostartManager, LinuxAutostart>();
    }
});
builder.ConfigureAppConfiguration((config) =>
    config.AddJsonFile(Application.AppSettings.Path, optional: true, reloadOnChange: true));
builder.ConfigureLogging((builder) =>
{
    builder.ClearProviders();
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddFilter("Microsoft", (level) => level >= LogLevel.Warning);
    builder.AddFilter("System", (level) => level >= LogLevel.Warning);
    builder.AddConsole((options) =>
        options.FormatterName = CustomConsoleFormatter.FormatterName);
    builder.Services.AddSingleton<ConsoleFormatter, CustomConsoleFormatter>();
});

RootCommand root = new(builder);
var result = root.Parse(args, new() { EnablePosixBundling = true });
return await result.InvokeAsync();
