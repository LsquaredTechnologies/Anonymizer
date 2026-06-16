using Microsoft.Extensions.Logging;

namespace Anonymizer;

internal sealed partial class AnonymizerService(PathProvider paths, Python.Runner runner, ILogger<AnonymizerService> logger)
{
    public async Task AnonymizeFilesAsync(IEnumerable<FileSystemInfo> filesOrDirs, CancellationToken cancellationToken = default)
    {
        foreach (var fileOrDir in filesOrDirs)
            await AnonymizeFilesAsync(fileOrDir, cancellationToken);
    }

    public async Task AnonymizeFilesAsync(FileSystemInfo fileOrDir, CancellationToken cancellationToken = default)
    {
        if (fileOrDir is DirectoryInfo dir)
        {
            await AnonymizeFilesAsync(dir.GetFiles("*.pdf", SearchOption.AllDirectories), cancellationToken);
        }
        else if (fileOrDir is FileInfo file)
        {
            await AnonymizeFileAsync(file, cancellationToken);
        }
    }

    public async Task AnonymizeFileAsync(FileInfo file, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            await LaunchProcessAsync(file, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task LaunchProcessAsync(FileInfo file, CancellationToken cancellationToken)
    {
        // Utilisation directe du répertoire prédéfini
        var result = await runner.ExecuteScriptAsync("anonymizer.py", [paths.ModelsDir, file.FullName], cancellationToken);

        if (result.IsSuccess)
        {
            LogProcessTerminated(file.Name);
        }
        else
        {
            LogProcessErrorOutput(file.Name, result.Error);
            throw new Exception($"Le traitement Python a échoué avec le code {result.ExitCode}: {result.Error}");
        }
    }

    [LoggerMessage(LogLevel.Information, "File {FileName} processed")]
    private partial void LogProcessTerminated(string fileName);

    [LoggerMessage(LogLevel.Error, "Error when processing {FileName}: {Error}")]
    private partial void LogProcessErrorOutput(string fileName, string error);

    // Maximum 4 files processed at same time!
    private readonly SemaphoreSlim _semaphore = new(4);
    private readonly ILogger _logger = logger;
}
