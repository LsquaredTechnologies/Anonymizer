using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;

using Anonymizer.Cli.Lifetime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli.Commands;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class InstallCommand : Command
{
    private const string HelpDesc = """
        Installs the application into the user directory.
        """;

    private readonly Option<bool> _noAutostartOption = new("--no-autostart")
    {
        Description = "Do not enable autostart after installation.",
        DefaultValueFactory = (_) => false,
    };

    public InstallCommand(IHostBuilder builder) : base("install", HelpDesc)
    {
        Add(_noAutostartOption);
        SetAction((parseResult) =>
        {
            var app = builder.Build();
            var noAutostart = parseResult.GetRequiredValue(_noAutostartOption);
            var autostart = noAutostart ? null : app.Services.GetRequiredService<IAutostartManager>();
            return Install(autostart);
        });
    }

    private static async Task Install(IAutostartManager? autostart)
    {
        using var alreadyRunning = SingleInstance.TryAcquire("Setup");
        ProcessManager.KillRunningInstances();

        var installDir = Application.Install.Dir;
        var currentExeFile = Application.File;
        Console.WriteLine($"✅ Installing {Application.Name} into: {installDir}");

        installDir.Create();

        var (payloadSize, payloadStart) = GetPayloadInfo(currentExeFile);
        var payloadZipFile = ExtractPayloadToTemp(currentExeFile, payloadStart, payloadSize);

        ExtractZipSafely(payloadZipFile, installDir);

        var anonymizerPath = CreateAnonymizerWithoutPayload(currentExeFile, installDir, payloadStart);

        Metadata metadata = new(DateTime.UtcNow, typeof(InstallCommand).Assembly.GetName().Version, anonymizerPath.FullName);
        await metadata.Save();

        RunDownload(anonymizerPath);

        autostart?.SetAutostart(true);

        Console.WriteLine("📁 Installation completed.");
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

    private const string Magic = "SETUP-PAYLOAD";
}
