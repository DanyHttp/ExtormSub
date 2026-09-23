using Microsoft.Win32;

namespace ExtormSub.App.Infrastructure;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights, no scheduled task).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ExtormSub";
    public const string MinimizedArg = "--minimized";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var command = $"\"{Environment.ProcessPath}\" {MinimizedArg}";
            if (key.GetValue(ValueName) as string != command) key.SetValue(ValueName, command);
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
