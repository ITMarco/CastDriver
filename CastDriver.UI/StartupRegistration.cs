using Microsoft.Win32;

namespace CastDriver.UI;

// Manages the "start with Windows" registry entry (HKCU\…\Run).
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CastDriver";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (value) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else       key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
