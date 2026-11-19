using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Quizzler.Core.Study;

namespace Quizzler.App.Services;

/// <summary>Callbacks the tray menu needs from the app.</summary>
internal interface ITrayHost
{
    void ShowQuiz();
    void Snooze(TimeSpan? duration); // null = until tomorrow morning
    void ClearSnooze();
    void OpenSettings();
    void ExitApp();
}

/// <summary>The notification-area icon: badge, tooltip, right-click menu, and fallback balloon notifications.</summary>
internal sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _startItem, _snoozeHourItem, _snoozeTomorrowItem, _resumeItem;
    private int _lastCount = -1;
    private bool _lastSnoozed;

    public TrayController(ITrayHost host)
    {
        _startItem = Item("Start quiz", (_, _) => host.ShowQuiz());
        _startItem.FontWeight = FontWeights.SemiBold;
        _snoozeHourItem = Item("Snooze for 1 hour", (_, _) => host.Snooze(TimeSpan.FromHours(1)));
        _snoozeTomorrowItem = Item("Snooze until tomorrow", (_, _) => host.Snooze(null));
        _resumeItem = Item("Resume reminders", (_, _) => host.ClearSnooze());

        var menu = new ContextMenu();
        menu.Items.Add(_startItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_snoozeHourItem);
        menu.Items.Add(_snoozeTomorrowItem);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings…", (_, _) => host.OpenSettings()));
        menu.Items.Add(Item("Exit", (_, _) => host.ExitApp()));

        _icon = new TaskbarIcon
        {
            ToolTipText = "Quizzler",
            Icon = TrayIconRenderer.RenderIcon(0, snoozed: false),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => host.ShowQuiz();
        _icon.TrayBalloonTipClicked += (_, _) => host.ShowQuiz();
        _icon.ForceCreate(false);
    }

    public void Update(DueSummary summary, DateTimeOffset? snoozedUntil, DateTimeOffset now)
    {
        var snoozed = snoozedUntil is { } until && until > now;
        if (summary.Total != _lastCount || snoozed != _lastSnoozed)
        {
            var previous = _icon.Icon;
            _icon.Icon = TrayIconRenderer.RenderIcon(summary.Total, snoozed);
            previous?.Dispose();
            _lastCount = summary.Total;
            _lastSnoozed = snoozed;
        }

        var status = summary.Total > 0
            ? NotificationPolicy.Message(summary)
            : summary.NextDueAt is { } next ? $"All caught up. Next review {TimeText.Until(next - now)}." : "All caught up.";
        if (snoozed) status += $"\nReminders snoozed until {snoozedUntil!.Value.LocalDateTime:t}.";
        _icon.ToolTipText = Truncate($"Quizzler\n{status}", 127); // Windows tooltip limit

        _startItem.Header = summary.Total > 0 ? $"Start quiz ({summary.Total} due)" : "Open Quizzler";
        _snoozeHourItem.Visibility = _snoozeTomorrowItem.Visibility = snoozed ? Visibility.Collapsed : Visibility.Visible;
        _resumeItem.Visibility = snoozed ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Plain balloon notification (title + text, click opens the quiz). Fallback when toasts are unavailable.</summary>
    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.ShowNotification(title, message, NotificationIcon.Info);
            Log.Info($"Notification: {title}: {message}");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to show notification.", ex);
        }
    }

    private static MenuItem Item(string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose() => _icon.Dispose();
}
