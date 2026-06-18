using System.Diagnostics;

namespace Anonymizer.Cli.Internals;

internal sealed class ProgressBar(int width = 30)
{
    public void Report(long downloaded, long total)
    {
        if (total <= 0) return;

        double progress = (double)downloaded / total;
        int filled = (int)(progress * _width);

        var now = DateTime.UtcNow;
        double dt = (now - _lastUpdate).TotalSeconds;
        double speed = dt > 0 ? (downloaded - _lastBytes) / dt : 0;

        _lastBytes = downloaded;
        _lastUpdate = now;

        double remaining = total - downloaded;
        double eta = speed > 0 ? remaining / speed : 0;

        string bar = BuildBar(filled);

        string percent = $"{progress * 100:0.0}%".PadLeft(6);
        string speedStr = $"{FormatBytes(speed)}/s".PadLeft(10);
        string etaStr = TimeSpan.FromSeconds(eta).ToString(@"hh\:mm\:ss");

        Console.CursorLeft = 0;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("▕");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write(bar);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("▏ ");
        Console.ResetColor();
        Console.Write($"{percent}  {speedStr}  ETA {etaStr}");
    }

    public void Finish()
    {
        _ = this;
        Console.CursorLeft = 0;
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✔ Download complete.");
        Console.ResetColor();
    }

    private string BuildBar(int filled)
    {
        Span<char> buffer = stackalloc char[_width];
        for (int i = 0; i < _width; i++)
            buffer[i] = i < filled ? Blocks[3] : Blocks[0];
        return new string(buffer);
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
        return $"{bytes:0.0} {units[i]}";
    }

    private static readonly char[] Blocks = ['░', '▒', '▓', '█'];

    private readonly int _width = width;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastBytes;
    private DateTime _lastUpdate = DateTime.UtcNow;
}
