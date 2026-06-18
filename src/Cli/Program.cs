using Anonymizer.Cli;
using Anonymizer.Cli.Commands;
using Anonymizer.Cli.Downloaders;
using Anonymizer.Cli.Python;

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
});
builder.ConfigureLogging((builder) =>
{
    builder.ClearProviders();
    builder.AddSimpleConsole((options) =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
    });
});

RootCommand root = new(builder);
var result = root.Parse(args, new() { EnablePosixBundling = true });
return await result.InvokeAsync();
