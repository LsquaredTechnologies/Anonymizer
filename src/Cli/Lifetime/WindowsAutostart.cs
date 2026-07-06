using System.Runtime.Versioning;

using Microsoft.Win32;

namespace Anonymizer.Lifetime;

[SupportedOSPlatform("Windows")]
internal sealed class WindowsAutostart : IAutostartManager
{
    public void SetAutostart(bool enable)
    {
        string executablePath = System.IO.Path.Combine(Application.Install.Path, "anonymizerw.exe");
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)!;
        if (enable)
            key.SetValue(Application.Name, $"\"{executablePath}\"");
        else
            key.DeleteValue(Application.Name, false);
    }
}
