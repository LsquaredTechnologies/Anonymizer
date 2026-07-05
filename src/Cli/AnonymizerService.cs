using Anonymizer.Cli.Core;

using Microsoft.Extensions.Logging;

namespace Anonymizer.Cli;

internal sealed partial class AnonymizerService(ILogger<AnonymizerService> logger)
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
        if (File.Exists(Path.ChangeExtension(file.FullName, ".anon.pdf"))) return;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            await RunPipeline(file, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task RunPipeline(FileInfo file, CancellationToken cancellationToken)
    {
        OutputPaths output = OutputPaths.From(file);
        await Task.Run(() =>
        {
            Console.WriteLine("[DEBUG] Avant RunPipeline()");
            var result = NativeMethods.RedactPdf(file.FullName, ResolveModelsDirectory().FullName, output.AnonymizedPdfPath);
            var error = result switch
            {
                0 => null,
                -1 => "Some paths are empty.",
                -2 => "Cannot read some paths.",
                -3 => "[Error] Pipeline execution failed",
                -4 => "[Error] Internal panic.",
                _ => throw new NotSupportedException("Invalid error code!"),
            };
            if (error is not null)
                LogProcessError(file.FullName, error);
            else
                LogProcessTerminated(file.FullName);
            Console.WriteLine("[DEBUG] Après RunPipeline()");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static DirectoryInfo ResolveModelsDirectory()
    {
        if (HasPiiModel(Application.Models.Dir))
            return Application.Models.Dir;

        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null)
        {
            DirectoryInfo candidate = new(Path.Combine(current.FullName, "models"));
            if (HasPiiModel(candidate))
                return candidate;

            current = current.Parent;
        }

        return Application.Models.Dir;
    }

    private static bool HasPiiModel(DirectoryInfo modelsDir) =>
        File.Exists(Path.Combine(modelsDir.FullName, "pii", "config.json")) &&
        File.Exists(Path.Combine(modelsDir.FullName, "pii", "model.onnx")) &&
        File.Exists(Path.Combine(modelsDir.FullName, "pii", "tokenizer.json"));

#pragma warning disable CA1822

    [LoggerMessage(LogLevel.Information, "File {FileName} processed")]
    private partial void LogProcessTerminated(string fileName);

    [LoggerMessage(LogLevel.Error, "Error when processing {FileName}: {Error}")]
    private partial void LogProcessError(string fileName, string error);

#pragma warning restore CA1822

    // Maximum 4 files processed at same time!
    private readonly SemaphoreSlim _semaphore = new(4);
    // private readonly RegexPiiDetector _regexDetector = new();
    private readonly ILogger _logger = logger;

    private readonly record struct OutputPaths(string AnonymizedPdfPath)
    {
        public static OutputPaths From(FileInfo file)
        {
            string basePath = Path.Combine(file.DirectoryName!, Path.GetFileNameWithoutExtension(file.Name));
            return new(
                AnonymizedPdfPath: basePath + ".anon.pdf");
        }
    }
}
