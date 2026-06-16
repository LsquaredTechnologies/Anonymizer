using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Extensions.Options;

namespace Anonymizer;

internal sealed class PathProvider(IOptions<PathOptions>? options)
{
    public static string BaseDir { get; } = Path.GetDirectoryName(Environment.ProcessPath)!;

    public static string ToolsDir => Path.Combine(BaseDir, "tools");

    public static string VirtualEnvDir => Path.Combine(BaseDir, ".venv");

    public static string AppSettingsPath => Path.Combine(BaseDir, "appsettings.json");

    public static string RequirementsPath => Path.Combine(BaseDir, "requirements.txt");

    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("Windows")]
    public static string GetInstallRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, Application.Name.ToLowerInvariant());
        }
        else if (OperatingSystem.IsLinux())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".local", "share", Application.Name.ToLowerInvariant());
        }
        throw new PlatformNotSupportedException();
    }

    public string ModelsDir => options?.Value?.Models is { } m
        ? (Path.IsPathRooted(m) ? m : Path.GetFullPath(Path.Combine(BaseDir, m)))
        : Path.Combine(BaseDir, "models");

    public string FilesDir => options?.Value?.Files is { } f
        ? (Path.IsPathRooted(f) ? f : Path.GetFullPath(Path.Combine(BaseDir, f)))
        : Path.Combine(BaseDir, "files");

    public string GetAbsoluteScriptPath(string scriptName) => Path.Combine(BaseDir, "scripts", scriptName);

    public static void SaveConfiguration(string filesPath, string modelsPath)
    {
        var config = new { models = modelsPath, files = filesPath };
        File.WriteAllText(AppSettingsPath, System.Text.Json.JsonSerializer.Serialize(config));
    }
}
