using System.CommandLine;

using Anonymizer.Cli.Downloaders;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli.Commands;

internal sealed class DownloadCommand : Command
{
    private const string HelpDesc = """
        Download PII and face-recognition models.
        """;

    public DownloadCommand(IHostBuilder builder) : base("download", HelpDesc) =>
        SetAction(async (r, cancellationToken) =>
        {
            var app = builder.Build();

            var piiDownloader = app.Services.GetRequiredService<PIIModelDownloader>();
            await piiDownloader.DownloadAsync(Models.PII.Name, Application.Models.PII.Dir, cancellationToken);

            var faceDownloader = app.Services.GetRequiredService<FaceModelDownloader>();
            await faceDownloader.DownloadAsync(Models.Face.RemoteUri, Application.Models.Face.Dir, cancellationToken);
        });
}
