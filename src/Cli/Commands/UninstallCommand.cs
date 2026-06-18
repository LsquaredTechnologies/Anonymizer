using System.CommandLine;
using System.Runtime.Versioning;

using Anonymizer.Cli.Lifetime;

namespace Anonymizer.Cli.Commands;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class UninstallCommand : Command
{
    private const string HelpDesc = """
        Uninstalls the application from the user directory.
        """;

    public UninstallCommand() : base("uninstall", HelpDesc) =>
        SetAction((parseResult) => Uninstall());

    private static void Uninstall()
    {
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
            installDir.Delete(recursive: true);
            Console.WriteLine("Uninstallation complete.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to uninstall: {ex.Message}");
        }
    }
}
