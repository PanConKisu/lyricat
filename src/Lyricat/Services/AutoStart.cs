using Microsoft.Win32;

namespace LyricsStatus.Services;

public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string Flag = "--startup";

    static string Command => $"\"{Environment.ProcessPath}\" {Flag}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Paths.AppName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Paths.AppName, Command);
        else key.DeleteValue(Paths.AppName, throwOnMissingValue: false);
    }

    // The portable .exe can be moved around, so keep the entry pointing at wherever it runs from now.
    public static void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(Paths.AppName) is string current && current != Command) key.SetValue(Paths.AppName, Command);
        }
        catch (Exception) { }
    }

    public static void Forget()
    {
        Set(false);
        foreach (var name in Secrets.UserFields.Append(Secrets.RefreshToken)) Secrets.Set(name, null);
        foreach (var folder in new[] { Paths.Data, Paths.Local })
        {
            try { Directory.Delete(folder, recursive: true); } catch (Exception) { }
            var publisher = Path.GetDirectoryName(folder);
            try { if (publisher is not null && !Directory.EnumerateFileSystemEntries(publisher).Any()) Directory.Delete(publisher); } catch (Exception) { }
        }
    }
}
