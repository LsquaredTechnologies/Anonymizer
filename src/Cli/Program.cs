using Anonymizer.Cli;
using Anonymizer.Cli.Commands;
using Anonymizer.Cli.Downloaders;
using Anonymizer.Cli.Lifetime;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using static Anonymizer.Cli.Internals.ConsoleA;

AttachToConsole();
Console.OutputEncoding = Console.InputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateDefaultBuilder();
builder.ConfigureServices((context, services) =>
{
    services.AddHttpClient<ModelDownloader>();
    services.AddSingleton<AnonymizerService>();

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
    builder.AddFilter((category, level) => category?.StartsWith("Microsoft.") is false || level >= LogLevel.Error);
    builder.AddSimpleConsole((options) =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
        options.IncludeScopes = false;
    });
});

RootCommand root = new(builder);
var result = root.Parse(args, new() { EnablePosixBundling = true });
return await result.InvokeAsync();
