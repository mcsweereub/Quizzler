using System.Windows;
using Microsoft.Win32;

namespace Quizzler.App.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Applies light/dark: the WPF Fluent theme (controls, title bar) plus the app's own colour
/// dictionary (Themes/Light.xaml or Dark.xaml). "System" follows Windows' app theme setting live.
/// </summary>
internal static class ThemeService
{
    private static readonly Uri LightUri = new("pack://application:,,,/Quizzler.App;component/Themes/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/Quizzler.App;component/Themes/Dark.xaml");
    private static AppTheme _preference = AppTheme.System;
    private static bool _watchingSystem;

    public static bool IsDark { get; private set; }

    /// <summary>Raised after the effective theme flips between light and dark.</summary>
    public static event EventHandler? Changed;

    public static AppTheme Parse(string? value) =>
        Enum.TryParse<AppTheme>(value, ignoreCase: true, out var theme) ? theme : AppTheme.System;

    public static void Apply(AppTheme preference)
    {
        _preference = preference;
        if (!_watchingSystem)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _watchingSystem = true;
        }
        Update();
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_preference == AppTheme.System && e.Category == UserPreferenceCategory.General)
            Application.Current?.Dispatcher.BeginInvoke(Update);
    }

    private static void Update()
    {
        var dark = _preference switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => WindowsUsesDarkApps(),
        };

        var app = Application.Current;
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;

        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source == LightUri || d.Source == DarkUri);
        var wanted = dark ? DarkUri : LightUri;
        if (existing?.Source != wanted)
        {
            if (existing is not null) dictionaries.Remove(existing);
            dictionaries.Add(new ResourceDictionary { Source = wanted });
        }

        var flipped = IsDark != dark;
        IsDark = dark;
        if (flipped) Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool WindowsUsesDarkApps()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
