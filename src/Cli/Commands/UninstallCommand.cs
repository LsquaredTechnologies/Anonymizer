using System.CommandLine;
using System.Runtime.Versioning;

using Anonymizer.Cli.Lifetime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli.Commands;

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
        using var alreadyRunning = SingleInstance.TryAcquire("Setup");

        DirectoryInfo installDir = Application.Install.Dir;
        if (!installDir.Exists)
        {
            Console.WriteLine($"No installation found at: {installDir}");
            return;
        }

        Console.WriteLine($"Uninstalling {Application.Name} from: {installDir}");

        ProcessManager.KillRunningInstances();

        try
        {
            autostart.SetAutostart(false);

            installDir.Delete(recursive: true);
            Console.WriteLine("Uninstallation complete.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to uninstall: {ex.Message}");
        }
    }
}
