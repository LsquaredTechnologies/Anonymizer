using System.CommandLine;
using System.Runtime.Versioning;

using Anonymizer.Cli.Lifetime;

namespace Anonymizer.Cli.Commands;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class UpdateCommand : Command
{
    private const string HelpDesc = """
        Updates the application in the user directory if a newer version is available.
        """;

    public UpdateCommand() : base("update", HelpDesc) =>
        SetAction((parseResult) => Update());

    private static async Task Update()
    {
        using var alreadyRunning = SingleInstance.TryAcquire("Setup");

        DirectoryInfo installDir = Application.Install.Dir;
        if (!installDir.Exists)
        {
            Console.WriteLine($"No installation found at: {installDir}");
            Console.WriteLine("Run 'anonymizer install' first.");
            return;
        }

        FileInfo metadataFile = Application.Metadata.File;
        if (!metadataFile.Exists)
        {
            Console.WriteLine("Installation metadata not found. Cannot determine installed version.");
            return;
        }

        var installedMetadata = await Metadata.Load();
        Version? installedVersion = installedMetadata.Version;

        Version currentVersion = typeof(UpdateCommand).Assembly.GetName().Version!;
        if (installedVersion is not null && installedVersion >= currentVersion)
        {
            Console.WriteLine("Already up to date.");
            return;
        }

        Console.WriteLine("A new version is available. Updating...");

        ProcessManager.KillRunningInstances();

        DirectoryInfo currentDir = Application.Base.Dir;
        foreach (var file in currentDir.GetFiles())
        {
            string dest = Path.Combine(installDir.FullName, file.Name);
            file.CopyTo(dest, overwrite: true);
        }

        Metadata metadata = new(DateTime.UtcNow, typeof(UpdateCommand).Assembly.GetName().Version, Application.File);
        await metadata.Save();

        Console.WriteLine("Update complete.");
    }
}
