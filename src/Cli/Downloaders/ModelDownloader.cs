using Anonymizer.Internals;

using Microsoft.Extensions.Logging;

namespace Anonymizer.Downloaders;

internal sealed partial class ModelDownloader(HttpClient http, ILogger<ModelDownloader> logger)
{
    public async Task DownloadAsync(Uri remoteUri, FileInfo outputFile, CancellationToken cancellationToken)
    {
        outputFile.Directory?.Create();
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
            await using var output = outputFile.Open(FileMode.Create, FileAccess.Write, FileShare.Read);

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
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            throw;
        }
    }

#pragma warning disable CA1822

    [LoggerMessage(LogLevel.Information, "ONNX model already exists at {Path}.")]
    private partial void LogAlreadyExists(string path);

    [LoggerMessage(LogLevel.Information, "Downloading ONNX model…")]
    private partial void LogStartDownload();

    [LoggerMessage(LogLevel.Warning, "Unable to download ONNX model.")]
    private partial void LogUnableToDownloadModel();

    [LoggerMessage(LogLevel.Error, "Failed to download ONNX model.")]
    private partial void LogFailure(Exception exception);

#pragma warning restore CA1822

    private readonly ILogger _logger = logger;
}
