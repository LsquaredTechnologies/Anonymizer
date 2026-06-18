using System.CommandLine;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Anonymizer.Cli;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class UpdateCommand : Command
{
    private const string HelpDesc = """
        Updates the application in the user directory if a newer version is available.
        """;

    public UpdateCommand() : base("update", HelpDesc) =>
        SetAction((parseResult) => Update());

    private static void Update()
    {
        string installDir = PathProvider.GetInstallRoot();
        if (!Directory.Exists(installDir))
        {
            Console.WriteLine($"No installation found at: {installDir}");
            Console.WriteLine("Run 'anonymizer install' first.");
            return;
        }

        string metadataPath = Path.Combine(installDir, "installed.json");
        if (!File.Exists(metadataPath))
        {
            Console.WriteLine("Installation metadata not found. Cannot determine installed version.");
            return;
        }

        // Read installed metadata
        var installed = JsonSerializer.Deserialize<InstalledMetadata>(File.ReadAllText(metadataPath));
        Version? installedVersion = installed?.Version;

        // Current version (from running assembly)
        Version currentVersion = typeof(UpdateCommand).Assembly.GetName().Version!;

        Console.WriteLine($"Installed version : {installedVersion}");
        Console.WriteLine($"Available version : {currentVersion}");

        if (installedVersion is not null && installedVersion >= currentVersion)
        {
            Console.WriteLine("Already up to date.");
            return;
        }

        Console.WriteLine("Updating application...");

        string currentDir = AppContext.BaseDirectory;

        foreach (string file in Directory.GetFiles(currentDir))
        {
            string dest = Path.Combine(installDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        InstalledMetadata metadata = new()
        {
            InstalledAt = DateTime.UtcNow,
            Version = currentVersion,
            Source = Environment.ProcessPath!
        };

        File.WriteAllText(metadataPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine("Update complete.");
    }

    private sealed class InstalledMetadata
    {
        public DateTime InstalledAt { get; init; }
        public Version? Version { get; init; }
        public string? Source { get; init; }
    }
}
