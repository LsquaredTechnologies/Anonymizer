using System.CommandLine;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Anonymizer.Cli;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class InstallCommand : Command
{
    private const string HelpDesc = """
        Installs the application into the user directory.
        """;

    public InstallCommand() : base("install", HelpDesc) =>
        SetAction((parseResult) => Install());

    private static void Install()
    {
        DirectoryInfo installDir = new(PathProvider.GetInstallRoot());
        DirectoryInfo currentDir = new(PathProvider.BaseDir);
        Console.WriteLine($"Installing {Application.Name} into: {installDir} / {currentDir}");

        if (installDir.FullName != currentDir.FullName)
        {
            installDir.Create();
            foreach (FileInfo file in currentDir.GetFiles())
            {
                string dest = Path.Combine(installDir.FullName, file.Name);
                File.Copy(file.FullName, dest, overwrite: true);
            }
        }

        var metadata = new
        {
            InstalledAt = DateTime.UtcNow,
            typeof(InstallCommand).Assembly.GetName().Version,
            Source = Environment.ProcessPath!,
        };
        string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(installDir.FullName, "installed.json"), json);
    }
}
