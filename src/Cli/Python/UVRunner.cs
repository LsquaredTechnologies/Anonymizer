namespace Anonymizer.Cli.Python;

internal sealed class UVRunner(FileInfo uvFile) : ExternalScriptRunner
{
    public override async Task<int> ExecuteScriptAsync(FileInfo scriptFile, string[] arguments, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        if (!scriptFile.Exists)
            throw new FileNotFoundException($"No Python script at {scriptFile.FullName}");

        var startInfo = CreateBaseStartInfo(uvFile, workingDirectory);
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(scriptFile.FullName);
        if (arguments is not null)
        {
            foreach (var arg in arguments)
                startInfo.ArgumentList.Add(arg);
        }

        return await RunProcessAsync(startInfo, cancellationToken);
    }
}
