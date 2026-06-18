using Anonymizer.Cli;
using Anonymizer.Cli.Commands;
using Anonymizer.Cli.Downloaders;
using Anonymizer.Cli.Python;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateDefaultBuilder();
builder.ConfigureServices((context, services) =>
{
    services.AddSingleton<PIIModelDownloader>();
    services.AddHttpClient<FaceModelDownloader>();
    services.AddSingleton((sp) => ActivatorUtilities.CreateInstance<UVRunner>(sp, Application.Tools.UV.File));
    services.AddSingleton<AnonymizerService>();
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
