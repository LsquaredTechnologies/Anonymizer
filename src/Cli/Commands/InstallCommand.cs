using System.CommandLine;
using System.Runtime.Versioning;

using Anonymizer.Cli.Lifetime;

namespace Anonymizer.Cli.Commands;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class InstallCommand : Command
{
    private const string HelpDesc = """
        Installs the application into the user directory.
        """;

    public InstallCommand() : base("install", HelpDesc) =>
        SetAction((parseResult) => Install());

    private static async Task Install()
    {
        ProcessManager.KillRunningInstances();

        var installDir = Application.Install.Dir;
        var currentDir = Application.Base.Dir;
        Console.WriteLine($"Installing {Application.Name} into: {installDir}");

        if (installDir.FullName != currentDir.FullName)
        {
            installDir.Create();
            foreach (FileInfo file in currentDir.GetFiles())
            {
                string dest = Path.Combine(installDir.FullName, file.Name);
                File.Copy(file.FullName, dest, overwrite: true);
            }
        }

        Metadata metadata = new(DateTime.UtcNow, typeof(UpdateCommand).Assembly.GetName().Version, Application.File);
        await metadata.Save();
    }
}
