using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

namespace Anonymizer.Python;

internal sealed class VirtualEnv(ILogger<VirtualEnv> logger)
{
    public string PythonExecutable => OperatingSystem.IsWindows()
        ? Path.Combine(PathProvider.VirtualEnvDir, "Scripts", "python.exe")
        : Path.Combine(PathProvider.VirtualEnvDir, "bin", "python");

    public string PipExecutable => OperatingSystem.IsWindows()
        ? Path.Combine(PathProvider.VirtualEnvDir, "Scripts", "pip.exe")
        : Path.Combine(PathProvider.VirtualEnvDir, "bin", "pip");

    public bool Exists => File.Exists(PythonExecutable);

    public async Task EnsureVenvAsync(CancellationToken cancellationToken)
    {
        if (Exists) return;

        logger.LogInformation("Création de l'environnement virtuel Python (.venv)...");
        Directory.CreateDirectory(PathProvider.VirtualEnvDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"-m venv \"{PathProvider.VirtualEnvDir}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo);
        if (process is not null) await process.WaitForExitAsync(cancellationToken);

        await InstallRequirementsAsync(cancellationToken);
    }

    private async Task InstallRequirementsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PathProvider.RequirementsPath))
        {
            logger.LogWarning("Fichier requirements.txt introuvable : {Path}", PathProvider.RequirementsPath);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = PipExecutable,
            Arguments = $"install -r \"{PathProvider.RequirementsPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo);
        if (process is not null) await process.WaitForExitAsync(cancellationToken);
    }
}
