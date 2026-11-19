namespace Quizzler.Core.Study;

public sealed record NotificationSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Minimum gap between reminders while questions stay due.</summary>
    public int ReminderIntervalMinutes { get; init; } = 60;

    /// <summary>No notifications from this local hour...</summary>
    public int QuietStartHour { get; init; } = 22;

    /// <summary>...until this local hour. Equal start and end means no quiet hours.</summary>
    public int QuietEndHour { get; init; } = 8;
}

/// <summary>Decides whether the tray app should pop a "questions due" notification right now.</summary>
public static class NotificationPolicy
{
    public static bool ShouldNotify(
        DueSummary summary,
        NotificationSettings settings,
        DateTimeOffset now,
        DateTimeOffset? lastNotifiedAt,
        DateTimeOffset? snoozedUntil,
        bool isStudying)
    {
        if (!settings.Enabled || summary.Total == 0 || isStudying) return false;
        if (snoozedUntil is { } snooze && now < snooze) return false;
        if (IsQuietTime(settings, now)) return false;
        return lastNotifiedAt is not { } last || now - last >= TimeSpan.FromMinutes(settings.ReminderIntervalMinutes);
    }

    public static bool IsQuietTime(NotificationSettings settings, DateTimeOffset now)
    {
        int start = settings.QuietStartHour, end = settings.QuietEndHour, hour = now.Hour;
        if (start == end) return false;
        return start < end
            ? hour >= start && hour < end   // e.g. 13-15
            : hour >= start || hour < end;  // wraps midnight, e.g. 22-8
    }

    /// <summary>When a "snooze until tomorrow" should end: the end of tonight's quiet hours (or 8am).</summary>
    public static DateTimeOffset NextMorning(NotificationSettings settings, DateTimeOffset now)
    {
        var hour = settings.QuietStartHour == settings.QuietEndHour ? 8 : settings.QuietEndHour;
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, now.Offset);
        return candidate > now ? candidate : candidate.AddDays(1);
    }

    public static string Message(DueSummary summary) => (summary.DueReviews, summary.NewAvailable) switch
    {
        (0, var n) => $"{n} new question{(n == 1 ? "" : "s")} ready to learn.",
        (var r, 0) => $"{r} question{(r == 1 ? "" : "s")} due for review.",
        var (r, n) => $"{r} due for review and {n} new.",
    };
}
