using Anonymizer.Cli.Internals;

using Microsoft.Extensions.Logging;

namespace Anonymizer.Cli.Downloaders;

internal sealed partial class FaceModelDownloader(HttpClient http, ILogger<FaceModelDownloader> logger)
{
    public async Task DownloadAsync(Uri remoteUri, DirectoryInfo outputDir, CancellationToken cancellationToken)
    {
        outputDir.Create();

        FileInfo localFile = new(Path.Join(outputDir.FullName, Path.GetFileName(remoteUri.AbsolutePath)));
        if (localFile.Exists)
        {
            LogAlreadyExists(localFile.FullName);
            return;
        }

        try
        {
            LogStartDownload();
            using var response = await http.GetAsync(
                remoteUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );

            long? totalBytes = response.Content.Headers.ContentLength;
            if (!response.IsSuccessStatusCode || totalBytes is null)
            {
                LogUnableToDownloadModel();
                return;
            }

            ProgressBar progress = new();

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = localFile.Open(FileMode.Create, FileAccess.Write);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                progress.Report(totalRead, totalBytes.Value);
            }

            progress.Finish();
            LogSuccess(outputDir.FullName);
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            throw;
        }
    }

#pragma warning disable CA1822

    [LoggerMessage(LogLevel.Information, "Face Detection ONNX model already exists at {Path}.")]
    private partial void LogAlreadyExists(string path);

    [LoggerMessage(LogLevel.Information, "Downloading Face Detection ONNX model…")]
    private partial void LogStartDownload();

    [LoggerMessage(LogLevel.Warning, "Unable to download Face Detection ONNX model.")]
    private partial void LogUnableToDownloadModel();

    [LoggerMessage(LogLevel.Information, "Model successfully downloaded to {Path}.")]
    private partial void LogSuccess(string path);

    [LoggerMessage(LogLevel.Error, "Failed to download Face Detection model.")]
    private partial void LogFailure(Exception exception);

#pragma warning restore CA1822

    private readonly ILogger _logger = logger;
}
