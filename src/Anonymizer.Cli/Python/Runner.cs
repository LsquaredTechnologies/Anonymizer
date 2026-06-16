using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace Anonymizer.Python;

internal sealed class Runner
{
    public bool IsUv { get; private set; }

    public Runner(PathProvider paths, VirtualEnv venv, ILogger<Runner> logger)
    {
        _logger = logger;
        IsUv = IsCommandAvailable("uv");
        _runner = IsUv ? new Internals.UVRunner(paths) : new Internals.StandardRunner(paths, venv);
    }

    public async Task<Result> ExecuteScriptAsync(string scriptName, string[] arguments, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Routage de l'exécution vers {RunnerType} pour le script : {ScriptName}",
            IsUv ? "UV" : "Python Standard", scriptName);

        var result = await _runner.ExecuteScriptAsync(scriptName, arguments, cancellationToken);
        if (!result.IsSuccess)
        {
            _logger.LogError("Le script {ScriptName} a échoué avec le code {ExitCode}. Erreur : {Error}", scriptName, result.ExitCode, result.Error);
        }

        return result;
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = PathProvider.ToolsDir,
            };
            process.Start();
            process.WaitForExit(1500);
            return process.ExitCode is 0;
        }
        catch
        {
            return false;
        }
    }

    private readonly BasePythonRunner _runner;
    private readonly ILogger<Runner> _logger;
}
