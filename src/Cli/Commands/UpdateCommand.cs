using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;

using Anonymizer.Cli.Lifetime;

namespace Anonymizer.Cli.Commands;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class UpdateCommand : Command
{
    private const string HelpDesc = """
        Updates the application in the user directory if a newer version is available.
        """;

    private const string Magic = "SETUP-PAYLOAD";

    public UpdateCommand() : base("update", HelpDesc) =>
        SetAction(_ => Update());

    private static async Task Update()
    {
        using var alreadyRunning = SingleInstance.TryAcquire("Setup");

        var installDir = Application.Install.Dir;
        if (!installDir.Exists)
        {
            Console.WriteLine($"No installation found at: {installDir}");
            Console.WriteLine("Run 'anonymizer install' first.");
            return;
        }

        var metadataFile = Application.Metadata.File;
        if (!metadataFile.Exists)
        {
            Console.WriteLine("Installation metadata not found. Cannot determine installed version.");
            return;
        }

        var installedMetadata = await Metadata.Load();
        Version? installedVersion = installedMetadata.Version;

        Version currentVersion = typeof(UpdateCommand).Assembly.GetName().Version!;
        if (installedVersion is not null && installedVersion >= currentVersion)
        {
            Console.WriteLine("Already up to date.");
            return;
        }

        Console.WriteLine("A new version is available. Updating...");

        ProcessManager.KillRunningInstances();

        string setupName = OperatingSystem.IsWindows() ? "setup.exe" : "setup";
        string releaseUrl =
            $"https://github.com/LsquaredTechnologies/Anonymizer/releases/latest/download/{setupName}";

        string tempPath = Path.Combine(Path.GetTempPath(), "anonymizer-update");
        DirectoryInfo tempDir = new(tempPath);
        if (tempDir.Exists)
            tempDir.Delete(recursive: true);
        tempDir.Create();

        FileInfo downloadedSetup = new(Path.Combine(tempPath, setupName));

        Console.WriteLine($"Downloading: {releaseUrl}");

        using (var http = new HttpClient())
        using (var stream = await http.GetStreamAsync(releaseUrl))
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

        Console.WriteLine("Update complete.");
    }

    private static (long payloadSize, long payloadStart) GetPayloadInfo(FileInfo exeFile)
    {
        using var fs = exeFile.Open(FileMode.Open, FileAccess.Read);

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
        FileInfo tempFile = new(Path.Combine(Path.GetTempPath(), "anonymizer-update-payload.zip"));

        using var fs = exeFile.Open(FileMode.Open, FileAccess.Read);
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

    private static FileInfo CreateAnonymizerWithoutPayload(FileInfo exeFile, DirectoryInfo installDir, long payloadStart)
    {
        FileInfo targetFile = new(Path.Combine(
            installDir.FullName,
            Path.ChangeExtension(Application.Name, OperatingSystem.IsWindows() ? ".exe" : string.Empty)
        ));

        using var input = exeFile.Open(FileMode.Open, FileAccess.Read);
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
}
