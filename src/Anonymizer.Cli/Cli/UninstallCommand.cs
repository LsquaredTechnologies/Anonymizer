using System.CommandLine;
using System.Runtime.Versioning;

using Anonymizer.Autostart;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class UninstallCommand : Command
{
    private const string HelpDesc = """
        Uninstalls the application from the user directory.
        """;

    public UninstallCommand(IHostBuilder builder) : base("uninstall", HelpDesc) =>
        SetAction((parseResult) =>
        {
            var app = builder.Build();
            var autostart = app.Services.GetRequiredService<IAutostartManager>();
            Uninstall(autostart);
        });

    private static void Uninstall(IAutostartManager autostart)
    {
        string installDir = PathProvider.GetInstallRoot();
        if (!Directory.Exists(installDir))
        {
            Console.WriteLine($"No installation found at: {installDir}");
            return;
        }

        Console.WriteLine($"Uninstalling {Application.Name} from: {installDir}");

        try
        {
            autostart.SetAutostart(false);
            Directory.Delete(installDir, recursive: true);
            Console.WriteLine("Uninstallation complete.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to uninstall: {ex.Message}");
        }
    }
}
