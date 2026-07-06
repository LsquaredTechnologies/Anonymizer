using System.Runtime.Versioning;

using Microsoft.Win32;

namespace Anonymizer.Lifetime;

[SupportedOSPlatform("Windows")]
internal sealed class WindowsAutostart : IAutostartManager
{
    public void SetAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)!;
        if (enable)
            key.SetValue(Application.Name, $"\"{Application.Path}\"");
        else
            key.DeleteValue(Application.Name, false);
    }
}
