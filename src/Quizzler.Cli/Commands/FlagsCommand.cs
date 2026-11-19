using System.ComponentModel;
using Quizzler.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Quizzler.Cli.Commands;

public sealed class FlagsCommand : Command<FlagsCommand.Settings>
{
    public sealed class Settings : DbSettings
    {
        [CommandOption("--resolve <QUESTION_ID>")]
        [Description("Mark all open flags on this question as resolved (after fixing it in content/).")]
        public string? Resolve { get; set; }

        [CommandOption("--all")]
        [Description("Include resolved flags.")]
        public bool All { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var db = QuizDatabase.Open(settings.ResolveDb());

        if (settings.Resolve is { } id)
        {
            var count = db.ResolveFlags(id, DateTimeOffset.Now);
            AnsiConsole.MarkupLine(count > 0
                ? $"[green]Resolved {count} flag(s) on {Markup.Escape(id)}.[/]"
                : $"[yellow]No open flags on {Markup.Escape(id)}.[/]");
            return 0;
        }

        var flags = db.GetFlags(settings.All);
        if (flags.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No flagged questions.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Question", "Flagged", "Note", "Prompt");
        foreach (var flag in flags)
        {
            var question = db.GetQuestion(flag.QuestionId);
            var when = flag.FlaggedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            if (flag.ResolvedAt is not null) when += " [green](resolved)[/]";
            table.AddRow(
                Markup.Escape(flag.QuestionId),
                when,
                Markup.Escape(flag.Note ?? ""),
                Markup.Escape(question?.Prompt ?? "(question no longer exists)"));
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Fix the questions in content/, run `quizzler import`, then `quizzler flags --resolve <id>`.[/]");
        return 0;
    }
}
