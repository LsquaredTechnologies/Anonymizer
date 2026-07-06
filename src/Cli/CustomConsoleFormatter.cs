using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Anonymizer.Cli;

internal sealed class CustomConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "Custom";

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (message is null) return;

        var timestamp = DateTimeOffset.Now.ToString("HH:mm:ss");
        textWriter.Write($"[{timestamp}] ");

        var color = GetColor(logEntry.LogLevel);
        textWriter.Write($"{color}{logEntry.LogLevel}{ResetColor}: ");

        textWriter.WriteLine(message);

        if (logEntry.Exception is not null)
            textWriter.WriteLine(logEntry.Exception.Message);
    }

    private static string GetColor(LogLevel level) => level switch
    {
        LogLevel.Trace => "\u001b[90m", // Gris fonce
        LogLevel.Debug => "\u001b[37m", // Gris clair
        LogLevel.Information => "\u001b[32m", // Vert
        LogLevel.Warning => "\u001b[33m", // Jaune
        LogLevel.Error => "\u001b[31m", // Rouge
        LogLevel.Critical => "\u001b[97;41m", // Blanc sur fond rouge
        _ => "\u001b[0m"
    };

    private const string ResetColor = "\u001b[0m";
}
