using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anonymizer;

internal sealed partial class FilesWatcher : BackgroundService
{
    public FilesWatcher(
        AnonymizerService anonymizer,
        IOptionsMonitor<FilesWatcherOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<FilesWatcher> logger)
    {
        _anonymizer = anonymizer;
        _options = options.CurrentValue;
        _lifetime = lifetime;
        _logger = logger;
        options.OnChange((newValue) =>
        {
            _options = newValue;
            RecreateWatcher(_cancellationToken);
        });
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var filesDir = _options.FilesDir;
        if (string.IsNullOrEmpty(filesDir))
        {
            _lifetime.StopApplication();
            LogUnsetFilesDir();
            return;
        }

        if (!Path.Exists(filesDir))
        {
            _lifetime.StopApplication();
            LogInexistentDir(filesDir);
            return;
        }

        _cancellationToken = cancellationToken;
        RecreateWatcher(cancellationToken);

        // first pass on startup
        await _anonymizer.AnonymizeFilesAsync(new DirectoryInfo(filesDir), cancellationToken);

        await base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        _tcs.TrySetResult();
        return base.StopAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only register for process termination!
        stoppingToken.Register(() => _tcs.TrySetResult());
        return Task.CompletedTask;
    }

    private void RecreateWatcher(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        if (cancellationToken.IsCancellationRequested) return;
        _watcher = new(_options.FilesDir!)
        {
            Filter = "*.pdf",
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _watcher.Created += async (_, e) =>
        {
            try
            {
                await Task.Delay(200, cancellationToken);

                LogFileFound(e.FullPath);
                FileInfo file = new(e.FullPath);
                await _anonymizer.AnonymizeFileAsync(file, cancellationToken);
            }
            catch (Exception ex)
            {
                LogErrorWhileProcessing(ex, e.FullPath);
            }
        };
    }

#pragma warning disable CA1822

    [LoggerMessage(LogLevel.Warning, "Please use \"config set filesdir <existing directory>\" to configure the application.")]
    private partial void LogUnsetFilesDir();

    [LoggerMessage(LogLevel.Warning, "Configured directory does not exist: {Path}.")]
    private partial void LogInexistentDir(string path);

    [LoggerMessage(LogLevel.Information, "File found: {FileName}.")]
    private partial void LogFileFound(string fileName);

    [LoggerMessage(LogLevel.Error, "Error when processing {FileName}.")]
    private partial void LogErrorWhileProcessing(Exception e, string fileName);

#pragma warning restore CA1822

    private readonly AnonymizerService _anonymizer;
    private readonly ILogger<FilesWatcher> _logger;
    private FilesWatcherOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private FileSystemWatcher? _watcher;
    private CancellationToken _cancellationToken;
    private readonly TaskCompletionSource _tcs = new();
}
