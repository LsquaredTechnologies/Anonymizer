using System.CommandLine;
using System.Runtime.Versioning;

using Anonymizer.Cli.Lifetime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli.Commands;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class InstallCommand : Command
{
    private const string HelpDesc = """
        Installs the application into the user directory.
        """;

    private readonly Option<bool> _noAutostartOption = new("--no-autostart")
    {
        Description = "Do not enable autostart after installation.",
        DefaultValueFactory = (_) => false,
    };

    public InstallCommand(IHostBuilder builder) : base("install", HelpDesc)
    {
        Add(_noAutostartOption);
        SetAction((parseResult) =>
        {
            var app = builder.Build();
            var noAutostart = parseResult.GetRequiredValue(_noAutostartOption);
            var autostart = noAutostart ? null : app.Services.GetRequiredService<IAutostartManager>();
            return Install(autostart);
        });
    }

    private static async Task Install(IAutostartManager? autostart)
    {
        using var alreadyRunning = SingleInstance.TryAcquire("Setup");
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

        autostart?.SetAutostart(true);
    }
}
