using Anonymizer.Cli.Python;

using Microsoft.Extensions.Logging;

namespace Anonymizer.Cli.Downloaders;

internal sealed partial class PIIModelDownloader(
    UVRunner runner,
    ILogger<PIIModelDownloader> logger)
{
    public async Task DownloadAsync(string model, DirectoryInfo outputDir, CancellationToken cancellationToken)
    {
        if (outputDir.Exists && outputDir.GetFiles().Length > 0)
        {
            LogAlreadyExists(outputDir.FullName);
            return;
        }

        LogStartDownload();

        var result = await runner.ExecuteScriptAsync(
            Application.Scripts.DownloadModel.File,
            arguments: ["--model", model, "--out", outputDir.FullName],
            Application.Scripts.Dir,
            cancellationToken
        );

        if (result is not 0)
        {
            LogFailure();
            return;
        }

        LogSuccess(outputDir.FullName);
    }

#pragma warning disable CA1822

    [LoggerMessage(LogLevel.Information, "PII model already exists at {Path}")]
    private partial void LogAlreadyExists(string path);

    [LoggerMessage(LogLevel.Information, "Starting PII model download script…")]
    private partial void LogStartDownload();

    [LoggerMessage(LogLevel.Information, "Successfully downloaded PII model to: {Path}")]
    private partial void LogSuccess(string path);

    [LoggerMessage(LogLevel.Error, "Error while downloading PII model")]
    private partial void LogFailure();

#pragma warning restore CA1822

    private readonly ILogger _logger = logger;
}
