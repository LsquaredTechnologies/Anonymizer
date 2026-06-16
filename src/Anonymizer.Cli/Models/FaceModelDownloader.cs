using Microsoft.Extensions.Logging;

namespace Anonymizer.Models;

internal sealed partial class FaceModelDownloader(HttpClient http, PathProvider paths, ILogger<FaceModelDownloader> logger)
{
    private const string FaceModelUrl =
        "https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/raw/master/models/onnx/version-RFB-320.onnx";

    private const string FaceModelFile = "version-RFB-320.onnx";

    public async Task<string> DownloadAsync(CancellationToken cancellationToken)
    {
        string facesDir = Path.Combine(paths.ModelsDir, "faces");
        Directory.CreateDirectory(facesDir);

        string outputPath = Path.Combine(facesDir, FaceModelFile);

        // --- CACHE ---
        if (File.Exists(outputPath))
        {
            LogAlreadyExists(outputPath);
            return outputPath;
        }

        LogStartDownload();

        try
        {
            using var response = await http.GetAsync(
                FaceModelUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );

            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

            var buffer = new byte[81920];
            int read;

            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            LogSuccess(outputPath);
            return outputPath;
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            throw;
        }
    }

    [LoggerMessage(LogLevel.Information, "Face Detection ONNX model already exists at {Path}")]
    private partial void LogAlreadyExists(string path);

    [LoggerMessage(LogLevel.Information, "Downloading Face Detection ONNX model (UltraFace)…")]
    private partial void LogStartDownload();

    [LoggerMessage(LogLevel.Information, "Model successfully downloaded to: {Path}")]
    private partial void LogSuccess(string path);

    [LoggerMessage(LogLevel.Error, "Failed to download face model")]
    private partial void LogFailure(Exception exception);

    private readonly ILogger _logger = logger;
}
