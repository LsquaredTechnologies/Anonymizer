using static System.IO.Path;

namespace Anonymizer.Cli;

internal static class Application
{
    internal static class Base
    {
        public static readonly string Path = GetDirectoryName(Environment.ProcessPath);

        public static readonly DirectoryInfo Dir = new(Path);
    }
}
