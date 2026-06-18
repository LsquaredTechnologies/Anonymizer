using static System.IO.Path;

namespace Anonymizer.Cli;

internal static class Application
{
    internal static class Base
    {
        public static readonly string Path = GetDirectoryName(Environment.ProcessPath)!;

        public static readonly DirectoryInfo Dir = new(Path);
    }

    internal static class Tools
    {
        private static readonly string Extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        public static readonly string Path = Join(Base.Path, "tools")!;

        public static readonly DirectoryInfo Dir = new(Path);

        internal static class UV
        {
            public static readonly string Path = Join(Tools.Path, ChangeExtension("uv", Extension));

            public static FileInfo File = new(Path);
        }
    }
}
