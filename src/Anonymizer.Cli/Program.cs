using Anonymizer;
using Anonymizer.Autostart;
using Anonymizer.Cli;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateDefaultBuilder();
builder.ConfigureServices((context, services) =>
{
    services.Configure<PathOptions>(context.Configuration);

    services.AddSingleton<AnonymizerService>();
    services.AddSingleton<PathProvider>();

    services.AddSingleton<Anonymizer.Models.PIIModelDownloader>();
    services.AddHttpClient<Anonymizer.Models.FaceModelDownloader>();

    services.AddSingleton<Anonymizer.Python.Runner>();
    services.AddSingleton<Anonymizer.Python.VirtualEnv>();

    if (OperatingSystem.IsWindows())
        services.AddSingleton<IAutostartManager, WindowsAutostart>();
    else if (OperatingSystem.IsLinux())
        services.AddSingleton<IAutostartManager, LinuxAutostart>();
});
builder.ConfigureAppConfiguration((config) =>
    config.AddJsonFile(PathProvider.AppSettingsPath, optional: true));
builder.ConfigureLogging((builder) =>
{
    builder.ClearProviders();
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
    });
});

if (WindowsServiceHelpers.IsWindowsService())
{
    builder.UseWindowsService();
    builder.ConfigureServices((services) => services.AddHostedService<WorkerService>());
    await builder.Build().RunAsync();
    return 0;
}
else
{
    RootCommand root = new(builder);
    var result = root.Parse(args, new() { EnablePosixBundling = true });
    return await result.InvokeAsync();
}
