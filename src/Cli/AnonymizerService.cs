using Anonymizer.Cli.Python;

using Microsoft.Extensions.Logging;

namespace Anonymizer.Cli;

internal sealed partial class AnonymizerService(UVRunner runner, ILogger<AnonymizerService> logger)
{
    public async Task AnonymizeFilesAsync(IEnumerable<FileSystemInfo> filesOrDirs, CancellationToken cancellationToken = default)
    {
        List<Task> tasks = [];
        foreach (var fileOrDir in filesOrDirs)
            tasks.Add(AnonymizeFilesAsync(fileOrDir, cancellationToken));

        await Task.WhenAll(tasks);
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
        // skip if already anonymized
        if (file.Name.EndsWith(".anon.pdf")) return;

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
        var resultCode = await runner.ExecuteScriptAsync(
            Application.Scripts.Anonymize.File,
            arguments: [Application.Models.Path, file.FullName],
            Application.Scripts.Dir,
            cancellationToken);
        if (resultCode is 0)
            LogProcessTerminated(file.Name);
        else
            LogProcessErrorOutput(file.Name);
    }

#pragma warning disable CA1822

    [LoggerMessage(LogLevel.Information, "File {FileName} processed")]
    private partial void LogProcessTerminated(string fileName);

    [LoggerMessage(LogLevel.Error, "Error when processing {FileName}")]
    private partial void LogProcessErrorOutput(string fileName);

#pragma warning restore CA1822

    // Maximum 4 files processed at same time!
    private readonly SemaphoreSlim _semaphore = new(4);
    private readonly ILogger _logger = logger;
}
