using System.IO;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Quizzler.App.Services;

/// <summary>What a notification button asks the app to do.</summary>
internal enum NotificationAction { Open, StartQuiz, SnoozeHour }

internal interface INotifier : IDisposable
{
    /// <summary>"Time for a quiz" reminder. Replaces any earlier reminder still on screen.</summary>
    void ShowDueReminder(string title, string message);

    /// <summary>One-off informational notification (e.g. new questions imported).</summary>
    void ShowInfo(string title, string message);

    /// <summary>Removes the reminder from the screen and Action Center (e.g. once the user starts studying).</summary>
    void ClearDueReminder();
}

/// <summary>
/// Windows toast notifications via the Windows App SDK, with "Start quiz" and "Snooze 1 hour" buttons.
/// Button clicks arrive on a background thread; <c>onAction</c> must marshal to the UI thread.
/// </summary>
internal sealed class ToastNotifier : INotifier
{
    private const string ActionKey = "action";
    private const string ReminderTag = "due";
    private const string ReminderGroup = "reminders";

    private readonly AppNotificationManager _manager = AppNotificationManager.Default;
    private readonly Action<NotificationAction> _onAction;

    private ToastNotifier(Action<NotificationAction> onAction)
    {
        _onAction = onAction;
        // The handler must be attached before Register(), or clicks that launched the app are lost.
        _manager.NotificationInvoked += (_, args) => _onAction(Parse(args.Arguments));

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "quizzler.ico");
        _manager.Register("Quizzler", new Uri(icon));
    }

    /// <summary>Returns null if toasts aren't available on this machine (the caller falls back to balloons).</summary>
    public static ToastNotifier? TryCreate(Action<NotificationAction> onAction)
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                Log.Info("Windows App SDK notifications not supported here; using tray balloons.");
                return null;
            }
            return new ToastNotifier(onAction);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't initialise toast notifications; using tray balloons.", ex);
            return null;
        }
    }

    /// <summary>If Windows launched this process because a toast button was clicked, which one.</summary>
    public static NotificationAction? GetLaunchAction()
    {
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            return activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs args
                ? Parse(args.Arguments)
                : null;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read activation arguments.", ex);
            return null;
        }
    }

    /// <summary>Removes this app's notification registration (used by the uninstaller).</summary>
    public static void UnregisterAll()
    {
        try { AppNotificationManager.Default.UnregisterAll(); }
        catch (Exception ex) { Log.Error("UnregisterAll failed.", ex); }
    }

    public void ShowDueReminder(string title, string message)
    {
        var notification = new AppNotificationBuilder()
            .AddArgument(ActionKey, nameof(NotificationAction.Open))
            .AddText(title)
            .AddText(message)
            .AddButton(new AppNotificationButton("Start quiz").AddArgument(ActionKey, nameof(NotificationAction.StartQuiz)))
            .AddButton(new AppNotificationButton("Snooze 1 hour").AddArgument(ActionKey, nameof(NotificationAction.SnoozeHour)))
            .SetTag(ReminderTag)
            .SetGroup(ReminderGroup)
            .BuildNotification();
        Show(notification, title, message);
    }

    public void ShowInfo(string title, string message)
    {
        var notification = new AppNotificationBuilder()
            .AddArgument(ActionKey, nameof(NotificationAction.Open))
            .AddText(title)
            .AddText(message)
            .BuildNotification();
        Show(notification, title, message);
    }

    public void ClearDueReminder()
    {
        _ = RemoveReminderAsync();

        async Task RemoveReminderAsync()
        {
            try { await _manager.RemoveByTagAndGroupAsync(ReminderTag, ReminderGroup); }
            catch (Exception ex) { Log.Error("Couldn't clear reminder toast.", ex); }
        }
    }

    private void Show(AppNotification notification, string title, string message)
    {
        try
        {
            _manager.Show(notification);
            Log.Info($"Toast (id {notification.Id}, setting {_manager.Setting}): {title}: {message}");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to show toast.", ex);
        }
    }

    private static NotificationAction Parse(IDictionary<string, string> arguments) =>
        arguments.TryGetValue(ActionKey, out var value) && Enum.TryParse<NotificationAction>(value, out var action)
            ? action
            : NotificationAction.Open;

    // Deliberately not calling Unregister() on exit: stale toasts in Action Center should still be able to launch the app.
    public void Dispose() { }
}

/// <summary>Fallback: plain tray balloons (click opens the quiz via the tray icon's balloon-click event).</summary>
internal sealed class BalloonNotifier(TrayController tray) : INotifier
{
    public void ShowDueReminder(string title, string message) => tray.ShowBalloon(title, message + " Click to start.");
    public void ShowInfo(string title, string message) => tray.ShowBalloon(title, message);
    public void ClearDueReminder() { }
    public void Dispose() { }
}
