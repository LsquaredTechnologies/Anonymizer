using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace Anonymizer.Lifetime;

internal sealed partial class ProcessManager(ILogger<ProcessManager> logger)
{
    public void KillRunningInstances()
    {
        foreach (var process in Process.GetProcessesByName(Application.Name))
        {
            try
            {
                LogStoppingProcess(process.Id);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                LogFailedToStopProcess(ex, process.Id);
            }
        }
    }

#pragma warning disable CA1822

    [LoggerMessage(LogLevel.Information, "Stopping running instance: PID {ProcessId}")]
    private partial void LogStoppingProcess(int processId);

    [LoggerMessage(LogLevel.Warning, "Failed to stop process {ProcessId}.")]
    private partial void LogFailedToStopProcess(Exception exception, int processId);

#pragma warning restore CA1822

    private readonly ILogger<ProcessManager> _logger = logger;
}
