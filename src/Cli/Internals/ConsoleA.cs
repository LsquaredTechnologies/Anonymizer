using System.Runtime.InteropServices;

namespace Anonymizer.Cli.Internals;

internal static partial class ConsoleA
{
    public static void AttachToConsole()
    {
        if (OperatingSystem.IsWindows())
        {
            AttachConsole(0xFFFFFFFF);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool AttachConsole(uint dwProcessId);
}
