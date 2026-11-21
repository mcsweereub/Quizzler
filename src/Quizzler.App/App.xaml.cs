using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Quizzler.App.Services;
using Quizzler.App.ViewModels;
using Quizzler.App.Views;
using Quizzler.Core.Study;
using Quizzler.Data;

namespace Quizzler.App;

/// <summary>
/// Tray-resident app. Lives in the notification area, checks every minute for due questions,
/// shows a badge + reminder notifications, and opens the quiz window on demand.
///
/// Command line:
///   --background                 start in the tray only (used by "Start with Windows")
///   --db &lt;path&gt;                  use a different database
///   --unregister-notifications   remove toast registration and exit (used by the uninstaller)
/// </summary>
public partial class App : Application, ITrayHost, ISettingsHost
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private SingleInstance? _instance;
    private QuizDatabase _db = null!;
    private QuizService _service = null!;
    private TrayController? _tray;
    private INotifier? _notifier;
    private DispatcherTimer? _timer;
    private QuizWindow? _window;
    private SettingsWindow? _settingsWindow;
    private StatsWindow? _statsWindow;
    private DateTimeOffset? _lastNotifiedAt;
    private DateTimeOffset? _snoozedUntil;

    /// <summary>False with --no-activate (automated UI testing): windows open without taking keyboard focus.</summary>
    private bool _activateWindows = true;

    public QuizDatabase Database => _db;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        if (e.Args.Contains("--unregister-notifications", StringComparer.OrdinalIgnoreCase))
        {
            ToastNotifier.UnregisterAll();
            Shutdown();
            return;
        }

        // Launched by clicking a toast while the app wasn't running?
        var launchAction = ToastNotifier.GetLaunchAction();
        _activateWindows = !e.Args.Contains("--no-activate", StringComparer.OrdinalIgnoreCase);
        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase)
                         || launchAction == NotificationAction.SnoozeHour;
        var dbPath = ArgValue(e.Args, "--db") ?? Environment.GetEnvironmentVariable("QUIZZLER_DB") ?? QuizDatabase.DefaultPath;

        _instance = SingleInstance.TryStart(dbPath, () => Dispatcher.BeginInvoke(ShowQuiz));
        if (_instance is null)
        {
            // Already running: bring its window up (unless this is just the login auto-start) and quit.
            if (!background) SingleInstance.SignalExisting(dbPath);
            Shutdown();
            return;
        }

        Log.Info($"Starting (db={dbPath}, background={background}, launchAction={launchAction?.ToString() ?? "none"}).");
        _db = QuizDatabase.Open(dbPath);
        _service = new QuizService(_db) { Client = "app" };
        _service.LogEvent(StudyEvent.AppStarted, background ? "background" : "foreground");
        if (launchAction is { } clicked) _service.LogEvent(StudyEvent.ReminderClicked, ActionName(clicked) + " (app was closed)");
        ThemeService.Apply(ThemeService.Parse(_db.GetSetting(SettingsViewModel.ThemeSettingKey)));

        var contentMessage = BundledContent.Sync(_db);
        ConfigureStartupOnFirstRun();

        _tray = new TrayController(this);
        _notifier = (INotifier?)ToastNotifier.TryCreate(action => Dispatcher.BeginInvoke(() => OnNotificationAction(action)))
                    ?? new BalloonNotifier(_tray);

        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        if (contentMessage is not null) _notifier.ShowInfo("Quizzler", contentMessage);
        if (launchAction == NotificationAction.SnoozeHour) Snooze(TimeSpan.FromHours(1));
        if (background) Refresh();
        else ShowQuiz();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _notifier?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Re-reads what's due, updates the tray, and notifies if the policy says so.</summary>
    private void Refresh()
    {
        if (_tray is null || _notifier is null) return;
        try
        {
            var now = _service.Now;
            if (_snoozedUntil is { } until && until <= now) _snoozedUntil = null;

            var summary = _service.GetDueSummary();
            _tray.Update(summary, _snoozedUntil, now);

            var isStudying = _window is { IsVisible: true };
            if (NotificationPolicy.ShouldNotify(summary, _db.GetNotificationSettings(), now, _lastNotifiedAt, _snoozedUntil, isStudying))
            {
                _notifier.ShowDueReminder("Time for a quick quiz", NotificationPolicy.Message(summary));
                _lastNotifiedAt = now;
                _service.LogEvent(StudyEvent.ReminderShown, $"{summary.DueReviews} reviews, {summary.NewAvailable} new");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Refresh failed.", ex);
        }
    }

    private void OnNotificationAction(NotificationAction action)
    {
        Log.Info($"Notification action: {action}.");
        _service.LogEvent(StudyEvent.ReminderClicked, ActionName(action));
        switch (action)
        {
            case NotificationAction.SnoozeHour:
                Snooze(TimeSpan.FromHours(1));
                break;
            default:
                ShowQuiz();
                break;
        }
    }

    // ------------------------------------------------------------------ ITrayHost

    public void ShowQuiz()
    {
        if (_window is { IsVisible: true } open)
        {
            // A finished window with fresh questions due gets replaced by a new session; otherwise just bring it forward.
            if (open.ViewModel.IsDone && _service.GetDueSummary().Total > 0)
            {
                open.Closed -= OnQuizWindowClosed;
                open.Close();
            }
            else
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                if (_activateWindows) open.Activate();
                return;
            }
        }

        _notifier?.ClearDueReminder();
        _window = new QuizWindow(new QuizViewModel(_service, OpenSettings, OpenStats), _activateWindows);
        _window.Closed += OnQuizWindowClosed;
        _window.Show();
        if (_activateWindows) _window.Activate();
        Refresh();
    }

    private void OnQuizWindowClosed(object? sender, EventArgs e)
    {
        _window = null;
        // Quit with questions still due: wait a full reminder interval before nagging.
        // Cleared everything: reset, so the next question that comes due (e.g. a relearn in 10 min) gets a prompt reminder.
        _lastNotifiedAt = _service.GetDueSummary().Total > 0 ? _service.Now : null;
        Refresh();
    }

    public void Snooze(TimeSpan? duration)
    {
        var now = _service.Now;
        _snoozedUntil = duration is { } d ? now + d : NotificationPolicy.NextMorning(_db.GetNotificationSettings(), now);
        _notifier?.ClearDueReminder();
        Log.Info($"Snoozed until {_snoozedUntil:g}.");
        _service.LogEvent(StudyEvent.Snoozed, duration is { } h ? $"{h.TotalHours:0.#}h" : "tomorrow");
        Refresh();
    }

    public void ClearSnooze()
    {
        _snoozedUntil = null;
        Refresh();
    }

    public void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            if (_activateWindows) _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(new SettingsViewModel(this)) { ShowActivated = _activateWindows };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        if (_activateWindows) _settingsWindow.Activate();
    }

    public void OpenStats()
    {
        if (_statsWindow is { IsVisible: true })
        {
            if (_statsWindow.WindowState == WindowState.Minimized) _statsWindow.WindowState = WindowState.Normal;
            if (_activateWindows) _statsWindow.Activate();
            return;
        }
        _statsWindow = new StatsWindow(new StatsViewModel(_service, _db.GetQuestionHashes)) { ShowActivated = _activateWindows };
        _statsWindow.Closed += (_, _) => _statsWindow = null;
        _statsWindow.Show();
        if (_activateWindows) _statsWindow.Activate();
    }

    public void ExitApp()
    {
        _statsWindow?.Close();
        _settingsWindow?.Close();
        _window?.Close();
        Shutdown();
    }

    // ------------------------------------------------------------------ ISettingsHost

    public void SendTestNotification() =>
        _notifier?.ShowDueReminder("Time for a quick quiz", "This is a test reminder. The buttons work too.");

    public void OpenDataFolder() => OpenInExplorer(Path.GetDirectoryName(Path.GetFullPath(_db.Path))!);

    public void OpenLog()
    {
        if (File.Exists(Log.FilePath))
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        else
            OpenInExplorer(Path.GetDirectoryName(Log.FilePath)!);
    }

    public void SettingsSaved()
    {
        // New reminder settings apply straight away rather than waiting for the next tick.
        _lastNotifiedAt = null;
        Refresh();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>First launch of an installed copy turns on "Start with Windows"; later launches keep the path current.</summary>
    private void ConfigureStartupOnFirstRun()
    {
        if (StartupRegistration.IsDevBuild) return;
        try
        {
            if (_db.GetSetting("startup_configured") is null)
            {
                StartupRegistration.Enable();
                _db.SetSetting("startup_configured", "1");
            }
            else
            {
                StartupRegistration.RefreshPathIfEnabled();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't configure start with Windows.", ex);
        }
    }

    private static void OpenInExplorer(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception.", e.Exception);
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to {Log.FilePath}.",
            "Quizzler", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static string ActionName(NotificationAction action) => action switch
    {
        NotificationAction.StartQuiz => "start",
        NotificationAction.SnoozeHour => "snooze",
        _ => "open",
    };

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
