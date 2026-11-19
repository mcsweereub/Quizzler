using System.ComponentModel;
using Quizzler.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Quizzler.Cli.Commands;

public sealed class SettingsCommand : Command<SettingsCommand.Settings>
{
    public sealed class Settings : DbSettings
    {
        [CommandOption("--new-per-day <N>")]
        [Description("Maximum brand-new questions introduced per day.")]
        public int? NewPerDay { get; set; }

        [CommandOption("--rollover-hour <HOUR>")]
        [Description("Local hour (0-23) when a new study day starts.")]
        public int? RolloverHour { get; set; }

        public override ValidationResult Validate()
        {
            if (NewPerDay is < 0) return ValidationResult.Error("--new-per-day must be 0 or more.");
            if (RolloverHour is < 0 or > 23) return ValidationResult.Error("--rollover-hour must be between 0 and 23.");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var db = QuizDatabase.Open(settings.ResolveDb());
        var current = db.GetStudySettings();

        if (settings.NewPerDay is not null || settings.RolloverHour is not null)
        {
            current = current with
            {
                NewPerDay = settings.NewPerDay ?? current.NewPerDay,
                DayRolloverHour = settings.RolloverHour ?? current.DayRolloverHour,
            };
            db.SaveStudySettings(current);
            AnsiConsole.MarkupLine("[green]Saved.[/]");
        }

        AnsiConsole.MarkupLine($"Database:         [grey]{Markup.Escape(db.Path)}[/]");
        AnsiConsole.MarkupLine($"New per day:      {current.NewPerDay}");
        AnsiConsole.MarkupLine($"Day starts at:    {current.DayRolloverHour:00}:00");
        return 0;
    }
}
