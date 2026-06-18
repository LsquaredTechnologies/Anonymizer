using System.CommandLine;
using System.Runtime.Versioning;

namespace Anonymizer.Cli;

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
        string installDir = PathProvider.GetInstallRoot();
        if (!Directory.Exists(installDir))
        {
            Console.WriteLine($"No installation found at: {installDir}");
            return;
        }

        Console.WriteLine($"Uninstalling {Application.Name} from: {installDir}");

        try
        {
            Directory.Delete(installDir, recursive: true);
            Console.WriteLine("Uninstallation complete.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to uninstall: {ex.Message}");
        }
    }
}
