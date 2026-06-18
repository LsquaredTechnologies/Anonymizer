using static System.IO.Path;

namespace Anonymizer.Cli;

internal static class Application
{
    internal static class Base
    {
        public static readonly string Path = GetDirectoryName(Environment.ProcessPath)!;

        public static readonly DirectoryInfo Dir = new(Path);
    }

    internal static class Models
    {
        public static readonly string Path = Join(Base.Path, "models")!;

        public static readonly DirectoryInfo Dir = new(Path);

        internal static class PII
        {
            public static readonly string File = Join(Models.Path, "pii");

            public static DirectoryInfo Dir = new(File);
        }

        internal static class Face
        {
            public static readonly string Path = Join(Models.Path, "face");

            public static DirectoryInfo Dir = new(Path);
        }
    }

    internal static class Scripts
    {
        public static readonly string Path = Join(Base.Path, "scripts")!;

        public static readonly DirectoryInfo Dir = new(Path);

        internal static class Anonymize
        {
            public static readonly string Path = Join(Scripts.Path, "anonymize.py");

            public static readonly FileInfo File = new(Path);
        }

        internal static class DownloadModel
        {
            public static readonly string Path = Join(Scripts.Path, "download-model.py");

            public static readonly FileInfo File = new(Path);
        }
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
