using Microsoft.Win32;

namespace Quizzler.App.Services;

/// <summary>
/// "Start with Windows" via the per-user Run key (no admin needed). The app launches with
/// --background so it sits in the tray instead of opening a window at login.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Quizzler";

    private static string ExePath => Environment.ProcessPath!;
    private static string Command => $"\"{ExePath}\" --background";

    /// <summary>
    /// True when running from a build output folder (bin\Debug, bin\Release). Dev builds never touch
    /// the registry automatically, so testing doesn't hijack the installed app's startup entry.
    /// </summary>
    public static bool IsDevBuild =>
        ExePath.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
        ExePath.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, Command);
        Log.Info($"Start with Windows enabled: {Command}");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Info("Start with Windows disabled.");
    }

    /// <summary>If the app has moved (e.g. reinstalled elsewhere), point the startup entry at this copy.</summary>
    public static void RefreshPathIfEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (key?.GetValue(ValueName) is string current && current != Command)
            Enable();
    }
}
