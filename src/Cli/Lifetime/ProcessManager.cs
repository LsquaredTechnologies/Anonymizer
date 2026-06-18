using System.Diagnostics;

namespace Anonymizer.Cli.Lifetime;

internal static class ProcessManager
{
    public static void KillRunningInstances()
    {
        foreach (var process in Process.GetProcessesByName(Application.Name))
        {
            try
            {
                Console.WriteLine($"Stopping running instance: PID {process.Id}");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to stop process {process.Id}: {ex.Message}");
            }
        }
    }
}
