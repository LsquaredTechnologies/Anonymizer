using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Anonymizer;

internal sealed class WorkerService : BackgroundService
{
    public WorkerService(AnonymizerService anonymizer, PathProvider paths, ILogger<WorkerService> logger)
    {
        _anonymizer = anonymizer;
        _logger = logger;
        _watcher = new(paths.FilesDir)
        {
            Filter = "*.pdf",
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _watcher.Created += async (_, e) =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(200, cancellationToken);
                    _logger.LogInformation("Nouveau fichier détecté : {File}", e.FullPath);
                    FileInfo file = new(e.FullPath);
                    _ = Task.Run(() => _anonymizer.AnonymizeFileAsync(file, cancellationToken), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erreur lors du traitement de {File}", e.FullPath);
                }
            });
        };

        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher.Dispose();
        _tcs.TrySetResult();
        return base.StopAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stoppingToken.Register(() => _tcs.TrySetResult());
        return Task.CompletedTask;
    }

    private readonly AnonymizerService _anonymizer;
    private readonly ILogger<WorkerService> _logger;
    private readonly FileSystemWatcher _watcher;
    private readonly TaskCompletionSource _tcs = new();
}
