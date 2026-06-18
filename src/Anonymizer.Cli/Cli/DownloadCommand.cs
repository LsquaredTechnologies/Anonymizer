using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli;

internal sealed class DownloadCommand : Command
{
    private const string HelpDesc = """
        Download PII and face-recognition models.
        """;

    public DownloadCommand(IHostBuilder builder) : base("download", HelpDesc) =>
        SetAction(async (r, cancellationToken) =>
        {
            var app = builder.Build();

            var downloadService = app.Services.GetRequiredService<Models.PIIModelDownloader>();
            await downloadService.DownloadAsync(cancellationToken);

            var downloader = app.Services.GetRequiredService<Models.FaceModelDownloader>();
            await downloader.DownloadAsync(cancellationToken);

            return 0;
        });
}
