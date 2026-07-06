using Anonymizer;
using Anonymizer.Commands;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

var builder = Host.CreateDefaultBuilder();
builder.ConfigureServices((context, services) =>
{
    services.AddSingleton<AnonymizerService>();
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

await StartCommand.ExecuteAsync(builder);
