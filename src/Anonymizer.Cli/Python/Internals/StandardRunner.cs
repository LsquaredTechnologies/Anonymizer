namespace Anonymizer.Python.Internals;

internal sealed class StandardRunner(PathProvider paths, VirtualEnv venv) : BasePythonRunner
{
    public override async Task<Result> ExecuteScriptAsync(string scriptName, string[] arguments, CancellationToken cancellationToken)
    {
        string scriptPath = paths.GetAbsoluteScriptPath(scriptName);
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Script Python introuvable : {scriptPath}");

        string executable = venv.Exists ? venv.PythonExecutable : "python";

        var startInfo = CreateBaseStartInfo(executable);
        startInfo.ArgumentList.Add(scriptPath);

        if (arguments is not null)
        {
            foreach (var arg in arguments)
                startInfo.ArgumentList.Add(arg);
        }

        return await RunProcessAsync(startInfo, cancellationToken);
    }
}
