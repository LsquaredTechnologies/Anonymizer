using System.Collections.ObjectModel;
using System.Runtime.Versioning;

using static System.IO.Path;

namespace Anonymizer.Cli;

internal static class Application
{
    public static readonly string Name = typeof(Application).Assembly.GetName().Name!;

    public static readonly string Path = Environment.ProcessPath!;

    public static readonly FileInfo File = new(Path);

    internal static class Base
    {
        public static readonly string Path = GetDirectoryName(Application.Path)!;

        public static readonly DirectoryInfo Dir = new(Path);
    }

    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("Windows")]
    internal static class Install
    {
        public static readonly string Path = GetInstallPath();

        public static readonly DirectoryInfo Dir = new(Path);

        private static string GetInstallPath()
        {
            if (OperatingSystem.IsWindows())
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Combine(local, Name.ToLowerInvariant());
            }
            else if (OperatingSystem.IsLinux())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Combine(home, ".local", "share", Name.ToLowerInvariant());
            }
            throw new PlatformNotSupportedException();
        }
    }

    internal static class Models
    {
        public static readonly string Path = Join(Base.Path, "models")!;

        public static readonly DirectoryInfo Dir = new(Path);

        internal static class PII
        {
            public static readonly string Path = Join(Models.Path, "pii");

            public static DirectoryInfo Dir = new(Path);
        }

        internal static class Face
        {
            public static readonly string Path = Join(Models.Path, "face");

            public static DirectoryInfo Dir = new(Path);
        }
    }

    internal static class AppSettings
    {
        public static readonly string Path = Join(Base.Path, "appsettings.json")!;
    }

    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("Windows")]
    internal static class Metadata
    {
        public static readonly string Path = Join(Install.Path, "metadata.json")!;

        public static FileInfo File = new(Path);
    }

    internal static class Config
    {
        public static readonly ReadOnlySet<string> ValidConfigKey = ["filesdir"];
    }
}
