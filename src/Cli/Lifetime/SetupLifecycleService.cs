using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;

using Anonymizer.Lifetime;

using Microsoft.Extensions.Logging;

namespace Anonymizer.Lifetime;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed partial class SetupLifecycleService(
    ProcessManager processManager,
    ILogger<SetupLifecycleService> logger,
    IAutostartManager? autostartManager = null)
{
    public async Task InstallAsync(bool enableAutostart)
    {
        using var alreadyRunning = SingleInstance.TryAcquire("Setup");
        processManager.KillRunningInstances();

        var installDir = Application.Install.Dir;
        var currentExeFile = Application.File;
        LogInstalling(Application.Name, installDir.FullName);

        installDir.Create();

        var (payloadSize, payloadStart) = GetPayloadInfo(currentExeFile);
        var payloadZipFile = ExtractPayloadToTemp(currentExeFile, payloadStart, payloadSize);

        ExtractZipSafely(payloadZipFile, installDir);

        var anonymizerPath = CreateAnonymizerWithoutPayload(currentExeFile, installDir, payloadStart);

        Metadata metadata = new(DateTime.UtcNow, typeof(SetupLifecycleService).Assembly.GetName().Version, anonymizerPath.FullName);
        await metadata.Save();

        RunDownload(anonymizerPath);

        if (enableAutostart)
            autostartManager?.SetAutostart(true);

        LogInstallationCompleted();
    }

    public void Uninstall()
    {
        using var alreadyRunning = SingleInstance.TryAcquire("Setup");

        DirectoryInfo installDir = Application.Install.Dir;
        if (!installDir.Exists)
        {
            LogNoInstallationFound(installDir.FullName);
            return;
        }

        LogUninstalling(Application.Name, installDir.FullName);

        processManager.KillRunningInstances();

        try
        {
            autostartManager?.SetAutostart(false);

            installDir.Delete(recursive: true);
            LogUninstallationComplete();
        }
        catch (Exception ex)
        {
            LogUninstallFailure(ex);
        }
    }

    public async Task UpdateAsync()
    {
        using var alreadyRunning = SingleInstance.TryAcquire("Setup");

        var installDir = Application.Install.Dir;
        if (!installDir.Exists)
        {
            LogNoInstallationFound(installDir.FullName);
            LogRunInstallFirst();
            return;
        }

        var metadataFile = Application.Metadata.File;
        if (!metadataFile.Exists)
        {
            LogMetadataNotFound();
            return;
        }

        var installedMetadata = await Metadata.Load();
        Version? installedVersion = installedMetadata.Version;

        Version currentVersion = typeof(SetupLifecycleService).Assembly.GetName().Version!;
        if (installedVersion is not null && installedVersion >= currentVersion)
        {
            LogAlreadyUpToDate();
            return;
        }

        LogUpdateStarting();

        processManager.KillRunningInstances();

        string tempPath = Path.Combine(Path.GetTempPath(), "anonymizer-update");
        DirectoryInfo tempDir = new(tempPath);
        if (tempDir.Exists)
            tempDir.Delete(recursive: true);
        tempDir.Create();

        string setupName = OperatingSystem.IsWindows() ? "setup.exe" : "setup";
        Uri releaseSetupUri = new(Models.BaseUri, setupName);
        FileInfo downloadedSetup = new(Path.Combine(tempPath, setupName));
        LogDownloading(releaseSetupUri.ToString());

        using (var http = new HttpClient())
        using (var stream = await http.GetStreamAsync(releaseSetupUri))
        using (var file = downloadedSetup.Open(FileMode.Create, FileAccess.Write))
            await stream.CopyToAsync(file);

        var (payloadSize, payloadStart) = GetPayloadInfo(downloadedSetup);

        var payloadZip = ExtractPayloadToTemp(downloadedSetup, payloadStart, payloadSize);
        ZipFile.ExtractToDirectory(payloadZip.OpenRead(), installDir.FullName, overwriteFiles: true);

        var anonymizerPath = CreateAnonymizerWithoutPayload(downloadedSetup, installDir, payloadStart);

        Metadata metadata = new(DateTime.UtcNow, currentVersion, anonymizerPath.FullName);
        await metadata.Save();
        RunDownload(anonymizerPath);

        tempDir.Delete(recursive: true);

        LogUpdateComplete();
    }

    private static (long payloadSize, long payloadStart) GetPayloadInfo(FileInfo exeFile)
    {
        using var fs = exeFile.Open(FileMode.Open, FileAccess.Read, FileShare.Read);

        fs.Seek(-8, SeekOrigin.End);
        Span<byte> sizeBytes = stackalloc byte[8];
        fs.ReadExactly(sizeBytes);
        long payloadSize = BitConverter.ToInt64(sizeBytes);

        var magicBytes = System.Text.Encoding.UTF8.GetBytes(Magic);
        fs.Seek(-(8 + magicBytes.Length), SeekOrigin.End);
        Span<byte> magicRead = stackalloc byte[magicBytes.Length];
        fs.ReadExactly(magicRead);

        if (!magicRead.SequenceEqual(magicBytes))
            throw new InvalidOperationException("Invalid setup file: payload footer not found.");

        long payloadStart = fs.Length - (8 + magicBytes.Length + payloadSize);
        return (payloadSize, payloadStart);
    }

    private static FileInfo ExtractPayloadToTemp(FileInfo exeFile, long payloadStart, long payloadSize)
    {
        FileInfo tempFile = new(Path.Combine(Path.GetTempPath(), "anonymizer-payload.zip"));

        using var fs = exeFile.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outFs = tempFile.Open(FileMode.Create, FileAccess.Write);

        fs.Seek(payloadStart, SeekOrigin.Begin);

        var buffer = new byte[81920];
        long remaining = payloadSize;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = fs.Read(buffer, 0, toRead);
            if (read is 0) break;
            outFs.Write(buffer, 0, read);
            remaining -= read;
        }

        return tempFile;
    }

    private static void ExtractZipSafely(FileInfo zipFile, DirectoryInfo installDir)
    {
        using var archive = ZipFile.OpenRead(zipFile.FullName);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.FullName.Equals("metadata.json", StringComparison.OrdinalIgnoreCase))
                continue;

            string destinationPath = Path.Combine(installDir.FullName, entry.FullName);

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            if (string.IsNullOrEmpty(entry.Name))
                continue;

            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static FileInfo CreateAnonymizerWithoutPayload(FileInfo exeFile, DirectoryInfo installDir, long payloadStart)
    {
        FileInfo targetFile = new(Path.Combine(
            installDir.FullName,
            Path.ChangeExtension(Application.Name, OperatingSystem.IsWindows() ? ".exe" : string.Empty)
        ));

        using var input = exeFile.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = targetFile.Open(FileMode.Create, FileAccess.Write);

        var buffer = new byte[81920];
        long remaining = payloadStart;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = input.Read(buffer, 0, toRead);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            remaining -= read;
        }

        if (!OperatingSystem.IsWindows())
            Process.Start("chmod", $"+x \"{targetFile}\"")?.WaitForExit();

        return targetFile;
    }

    private static void RunDownload(FileInfo anonymizerFile)
    {
        var psi = new ProcessStartInfo
        {
            FileName = anonymizerFile.FullName,
            ArgumentList = { "download" },
            WorkingDirectory = anonymizerFile.Directory!.FullName,
            UseShellExecute = false,
        };

        var p = Process.Start(psi);
        p?.WaitForExit();
    }

#pragma warning disable CA1822

    [LoggerMessage(LogLevel.Information, "Installing {AppName} into: {InstallDir}")]
    private partial void LogInstalling(string appName, string installDir);

    [LoggerMessage(LogLevel.Information, "Installation completed.")]
    private partial void LogInstallationCompleted();

    [LoggerMessage(LogLevel.Warning, "No installation found at: {InstallDir}")]
    private partial void LogNoInstallationFound(string installDir);

    [LoggerMessage(LogLevel.Information, "Run 'anonymizer install' first.")]
    private partial void LogRunInstallFirst();

    [LoggerMessage(LogLevel.Warning, "Installation metadata not found. Cannot determine installed version.")]
    private partial void LogMetadataNotFound();

    [LoggerMessage(LogLevel.Information, "Already up to date.")]
    private partial void LogAlreadyUpToDate();

    [LoggerMessage(LogLevel.Information, "A new version is available. Updating...")]
    private partial void LogUpdateStarting();

    [LoggerMessage(LogLevel.Information, "Downloading: {ReleaseSetupUri}")]
    private partial void LogDownloading(string releaseSetupUri);

    [LoggerMessage(LogLevel.Information, "Update complete.")]
    private partial void LogUpdateComplete();

    [LoggerMessage(LogLevel.Information, "Uninstalling {AppName} from: {InstallDir}")]
    private partial void LogUninstalling(string appName, string installDir);

    [LoggerMessage(LogLevel.Information, "Uninstallation complete.")]
    private partial void LogUninstallationComplete();

    [LoggerMessage(LogLevel.Error, "Failed to uninstall.")]
    private partial void LogUninstallFailure(Exception exception);

#pragma warning restore CA1822

    private readonly ILogger<SetupLifecycleService> _logger = logger;
    private const string Magic = "SETUP-PAYLOAD";
}
