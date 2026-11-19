using System.ComponentModel;
using System.Text.RegularExpressions;
using Quizzler.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Quizzler.Cli.Commands;

public sealed partial class SettingsCommand : Command<SettingsCommand.Settings>
{
    public sealed class Settings : DbSettings
    {
        [CommandOption("--new-per-day <N>")]
        [Description("Maximum brand-new questions introduced per day.")]
        public int? NewPerDay { get; set; }

        [CommandOption("--rollover-hour <HOUR>")]
        [Description("Local hour (0-23) when a new study day starts.")]
        public int? RolloverHour { get; set; }

        [CommandOption("--notifications <on|off>")]
        [Description("Turn reminder notifications on or off.")]
        public string? Notifications { get; set; }

        [CommandOption("--reminder-minutes <N>")]
        [Description("Minimum minutes between reminders while questions are due.")]
        public int? ReminderMinutes { get; set; }

        [CommandOption("--quiet-hours <START-END>")]
        [Description("No notifications between these local hours, e.g. 22-8. Use 0-0 for none.")]
        public string? QuietHours { get; set; }

        public override ValidationResult Validate()
        {
            if (NewPerDay is < 0) return ValidationResult.Error("--new-per-day must be 0 or more.");
            if (RolloverHour is < 0 or > 23) return ValidationResult.Error("--rollover-hour must be between 0 and 23.");
            if (Notifications is not null and not ("on" or "off")) return ValidationResult.Error("--notifications must be 'on' or 'off'.");
            if (ReminderMinutes is < 1) return ValidationResult.Error("--reminder-minutes must be 1 or more.");
            if (QuietHours is not null && ParseQuietHours(QuietHours) is null)
                return ValidationResult.Error("--quiet-hours must look like 22-8 (hours 0-23).");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var db = QuizDatabase.Open(settings.ResolveDb());
        var study = db.GetStudySettings();
        var notify = db.GetNotificationSettings();
        var changed = false;

        if (settings.NewPerDay is not null || settings.RolloverHour is not null)
        {
            study = study with
            {
                NewPerDay = settings.NewPerDay ?? study.NewPerDay,
                DayRolloverHour = settings.RolloverHour ?? study.DayRolloverHour,
            };
            db.SaveStudySettings(study);
            changed = true;
        }

        if (settings.Notifications is not null || settings.ReminderMinutes is not null || settings.QuietHours is not null)
        {
            var quiet = settings.QuietHours is { } q ? ParseQuietHours(q)!.Value : (notify.QuietStartHour, notify.QuietEndHour);
            notify = notify with
            {
                Enabled = settings.Notifications is { } n ? n == "on" : notify.Enabled,
                ReminderIntervalMinutes = settings.ReminderMinutes ?? notify.ReminderIntervalMinutes,
                QuietStartHour = quiet.Item1,
                QuietEndHour = quiet.Item2,
            };
            db.SaveNotificationSettings(notify);
            changed = true;
        }

        if (changed) AnsiConsole.MarkupLine("[green]Saved.[/] [grey](The tray app picks changes up within a minute.)[/]");

        AnsiConsole.MarkupLine($"Database:         [grey]{Markup.Escape(db.Path)}[/]");
        AnsiConsole.MarkupLine($"New per day:      {study.NewPerDay}");
        AnsiConsole.MarkupLine($"Day starts at:    {study.DayRolloverHour:00}:00");
        AnsiConsole.MarkupLine($"Notifications:    {(notify.Enabled ? "on" : "off")}");
        AnsiConsole.MarkupLine($"Remind every:     {notify.ReminderIntervalMinutes} min while questions are due");
        AnsiConsole.MarkupLine(notify.QuietStartHour == notify.QuietEndHour
            ? "Quiet hours:      none"
            : $"Quiet hours:      {notify.QuietStartHour:00}:00 to {notify.QuietEndHour:00}:00");
        return 0;
    }

    private static (int, int)? ParseQuietHours(string value)
    {
        var m = QuietHoursPattern().Match(value.Trim());
        if (!m.Success) return null;
        int start = int.Parse(m.Groups[1].Value), end = int.Parse(m.Groups[2].Value);
        return start is >= 0 and <= 23 && end is >= 0 and <= 23 ? (start, end) : null;
    }

    [GeneratedRegex(@"^(\d{1,2})\s*-\s*(\d{1,2})$")]
    private static partial Regex QuietHoursPattern();
}
