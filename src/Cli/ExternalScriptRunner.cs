using System.Diagnostics;
using System.Text;

namespace Anonymizer.Cli;

internal abstract class ExternalScriptRunner
{
    public abstract Task<int> ExecuteScriptAsync(FileInfo scriptFile, string[] arguments, DirectoryInfo workingDirectory, CancellationToken cancellationToken);

    protected static async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Console.WriteLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(e.Data);
            Console.ForegroundColor = previous;
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return process.ExitCode;
    }

    protected static ProcessStartInfo CreateBaseStartInfo(FileInfo exeFile, DirectoryInfo? workingDir = null) => new()
    {
        FileName = exeFile.FullName,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        WorkingDirectory = workingDir?.FullName ?? Application.Base.Path,
    };
}
