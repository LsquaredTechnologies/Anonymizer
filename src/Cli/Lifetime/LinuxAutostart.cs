using System.Runtime.Versioning;

namespace Anonymizer.Lifetime;

[SupportedOSPlatform("Linux")]
internal sealed class LinuxAutostart : IAutostartManager
{
    public LinuxAutostart()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string autostartDir = Path.Combine(home, ".config", "autostart");
        Directory.CreateDirectory(autostartDir);
        _desktopFilePath = Path.Combine(autostartDir, $"{Application.Name}.desktop");
    }

    public void SetAutostart(bool enable)
    {
        if (enable)
        {
            string executablePath = Path.Combine(Application.Install.Path, "anonymizer");
            var content = $"""
                [Desktop Entry]
                Type=Application
                Name={Application.Name}
                Exec={executablePath} start
                Hidden=false
                """;
            File.WriteAllText(_desktopFilePath, content);
        }
        else if (File.Exists(_desktopFilePath))
        {
            File.Delete(_desktopFilePath);
        }
    }

    private readonly string _desktopFilePath;
}
