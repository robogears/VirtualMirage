using Microsoft.Win32;

namespace VirtualMirage;

/// <summary>Run-at-login via the per-user HKCU Run key (no admin required).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VirtualMirage";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (k is null) return;

            if (enabled)
            {
                string exe = Environment.ProcessPath ?? Application.ExecutablePath;
                k.SetValue(ValueName, $"\"{exe}\"");
                Log.Info($"Autostart enabled -> {exe}");
            }
            else
            {
                k.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Autostart disabled.");
            }
        }
        catch (Exception ex) { Log.Error("Autostart.Set failed", ex); }
    }
}
