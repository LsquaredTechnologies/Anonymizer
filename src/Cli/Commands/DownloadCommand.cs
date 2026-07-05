using System.CommandLine;
using System.IO.Compression;

using Anonymizer.Cli.Downloaders;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli.Commands;

internal sealed class DownloadCommand : Command
{
    private const string HelpDesc = """
        Download PII NER and face-recognition models.
        """;

    public DownloadCommand(IHostBuilder builder) : base("download", HelpDesc) =>
        SetAction(async (r, cancellationToken) =>
        {
            var app = builder.Build();

            var modelDownloader = app.Services.GetRequiredService<ModelDownloader>();
            var piiModelTask = DownloadAndUncompress(modelDownloader, Models.PII.Model.RemoteUri, Application.Models.PII.Dir, cancellationToken);
            var faceModelTask = DownloadAndUncompress(modelDownloader, Models.Face.Model.RemoteUri, Application.Models.Face.Dir, cancellationToken);
            await Task.WhenAll(piiModelTask, faceModelTask);
        });

    private static async Task DownloadAndUncompress(ModelDownloader modelDownloader, Uri remoteUri, DirectoryInfo outputDir, CancellationToken cancellationToken)
    {
        DirectoryInfo? modelsRoot = outputDir.Parent;
        if (modelsRoot is null)
            throw new InvalidOperationException("Unable to resolve models root directory.");

        string tempDirPath = Path.Combine(Path.GetTempPath(), "anonymizer-models");
        Directory.CreateDirectory(tempDirPath);

        string archiveName = Path.GetFileName(remoteUri.LocalPath);
        FileInfo archiveFile = new(Path.Combine(tempDirPath, archiveName));
        if (archiveFile.Exists)
            archiveFile.Delete();

        try
        {
            await modelDownloader.DownloadAsync(remoteUri, archiveFile, cancellationToken);
            ZipFile.ExtractToDirectory(archiveFile.FullName, modelsRoot.FullName, overwriteFiles: true);
        }
        finally
        {
            if (archiveFile.Exists)
                archiveFile.Delete();
        }

        EnsureExpectedFiles(remoteUri, outputDir);
    }

    private static void EnsureExpectedFiles(Uri remoteUri, DirectoryInfo outputDir)
    {
        string archiveName = Path.GetFileName(remoteUri.LocalPath);
        if (archiveName.StartsWith("pii_", StringComparison.OrdinalIgnoreCase))
        {
            string[] required = ["config.json", "model.onnx", "tokenizer.json"];
            foreach (string fileName in required)
            {
                if (!File.Exists(Path.Combine(outputDir.FullName, fileName)))
                    throw new FileNotFoundException($"Missing extracted PII model file: {fileName}");
            }
            return;
        }

        if (archiveName.StartsWith("face_", StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(Path.Combine(outputDir.FullName, "model.onnx")))
        {
            throw new FileNotFoundException("Missing extracted face model file: model.onnx");
        }
    }
}

