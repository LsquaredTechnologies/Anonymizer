using System.Diagnostics;
using System.Text;

namespace Anonymizer.Python;

internal abstract class BasePythonRunner
{
    public abstract Task<Result> ExecuteScriptAsync(string scriptName, string[] arguments, CancellationToken cancellationToken);
    
    protected static async Task<Result> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) outputBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) errorBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return new Result(process.ExitCode, outputBuilder.ToString().Trim(), errorBuilder.ToString().Trim());
    }

    protected static ProcessStartInfo CreateBaseStartInfo(string executable) => new()
    {
        FileName = executable,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        WorkingDirectory = PathProvider.BaseDir
    };
}
