using Microsoft.Extensions.Logging;

namespace Anonymizer.Models;

internal sealed partial class PIIModelDownloader(
    Python.Runner runner,
    Python.VirtualEnv venv,
    PathProvider paths,
    ILogger<PIIModelDownloader> logger)
{
    private const string Model = "yalen-ai/distilbert_pii_ner_yalen";

    public async Task<string> DownloadAsync(CancellationToken cancellationToken)
    {
        string piiDir = Path.Join(paths.ModelsDir, "pii");

        // --- CACHE ---
        if (Directory.Exists(piiDir) && Directory.GetFiles(piiDir).Length > 0)
        {
            LogAlreadyExists(piiDir);
            return piiDir;
        }

        if (!runner.IsUv)
        {
            LogUvMissing();
            await venv.EnsureVenvAsync(cancellationToken);
        }

        LogStartDownload();

        string[] arguments = ["--model", Model, "--out", piiDir];

        var result = await runner.ExecuteScriptAsync(
            "download_model.py",
            arguments,
            cancellationToken
        );

        if (!result.IsSuccess)
        {
            LogFailure(result.Error);
            throw new Exception($"Error while executing download_model.py: {result.Error}");
        }

        LogSuccess(piiDir);
        return piiDir;
    }

    [LoggerMessage(LogLevel.Information, "PII model already exists at {Path}")]
    private partial void LogAlreadyExists(string path);

    [LoggerMessage(LogLevel.Information, "‘uv’ tool not found. Verifying or initializing the local virtual environment…")]
    private partial void LogUvMissing();

    [LoggerMessage(LogLevel.Information, "Starting ONNX model download script…")]
    private partial void LogStartDownload();

    [LoggerMessage(LogLevel.Information, "Successfully downloaded model to: {Path}")]
    private partial void LogSuccess(string path);

    [LoggerMessage(LogLevel.Error, "Error while downloading model: {Error}")]
    private partial void LogFailure(string error);

    private readonly ILogger _logger = logger;
}
