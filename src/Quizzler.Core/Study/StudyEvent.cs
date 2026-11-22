namespace Quizzler.Core.Study;

/// <summary>
/// Things worth knowing about how Quizzler is used, beyond individual answers: do reminders lead to study,
/// how often is it snoozed, are sessions finished. Stored in the events table with an optional detail string.
/// </summary>
public static class StudyEvent
{
    public const string AppStarted = "app_started";
    public const string ReminderShown = "reminder_shown";

    /// <summary>A reminder button or body was clicked. Detail: "start", "snooze" or "open".</summary>
    public const string ReminderClicked = "reminder_clicked";

    /// <summary>Detail: "1h" / "tomorrow", plus where from ("tray" / "reminder").</summary>
    public const string Snoozed = "snoozed";

    /// <summary>Detail: "{reviews} reviews, {new} new".</summary>
    public const string SessionStarted = "session_started";

    /// <summary>Detail: "{answered}/{total} answered, {correct} correct".</summary>
    public const string SessionEnded = "session_ended";

    /// <summary>A coding exercise was run (not yet submitted). Detail: "{question id}: {passed}/{total} passed" or "{id}: compile error".</summary>
    public const string ExerciseRun = "exercise_run";

    /// <summary>"Explain my mistake" was used. Detail: "{question id}" plus " failed: {reason}" when it didn't work.</summary>
    public const string ExplanationRequested = "explanation_requested";
}

/// <summary>A row from the events table.</summary>
public sealed record StudyEventLog(DateTimeOffset At, string Kind, string? Detail);
