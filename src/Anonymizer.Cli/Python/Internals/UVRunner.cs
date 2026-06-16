namespace Anonymizer.Python.Internals;

internal sealed class UVRunner(PathProvider paths) : BasePythonRunner
{
    public override async Task<Result> ExecuteScriptAsync(string scriptName, string[] arguments, CancellationToken cancellationToken)
    {
        string scriptPath = paths.GetAbsoluteScriptPath(scriptName);
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Script Python introuvable : {scriptPath}");

        var startInfo = CreateBaseStartInfo("uv");
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(scriptPath);

        if (arguments is not null)
        {
            foreach (var arg in arguments)
                startInfo.ArgumentList.Add(arg);
        }

        return await RunProcessAsync(startInfo, cancellationToken);
    }
}
