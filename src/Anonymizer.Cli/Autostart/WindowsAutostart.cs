using System.Runtime.Versioning;

using Microsoft.Win32;

namespace Anonymizer.Autostart;

[SupportedOSPlatform("Windows")]
internal sealed class WindowsAutostart : IAutostartManager
{
    public void SetAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)!;
        if (enable)
        {
            var execPath = Environment.ProcessPath!;
            key.SetValue(Application.Name, $"\"{execPath}\"");
        }
        else
        {
            key.DeleteValue(Application.Name, false);
        }
    }
}
