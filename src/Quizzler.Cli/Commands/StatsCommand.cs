using Quizzler.Core.Study;
using Quizzler.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Quizzler.Cli.Commands;

/// <summary>Terminal version of the app's stats window (same numbers, from <see cref="StatsCalculator"/>).</summary>
public sealed class StatsCommand : Command<DbSettings>
{
    public override int Execute(CommandContext context, DbSettings settings, CancellationToken cancellationToken)
    {
        var db = QuizDatabase.Open(settings.ResolveDb());
        var service = new QuizService(db);
        var now = service.Now;

        if (db.GetActiveQuestions().Count == 0)
        {
            AnsiConsole.MarkupLine("No questions yet. Run [yellow]quizzler import[/] first.");
            return 0;
        }

        var r = service.GetStats(db.GetQuestionHashes());
        var summary = service.GetDueSummary();

        var overview = new Grid().AddColumn().AddColumn();
        overview.AddRow("Streak", $"{r.CurrentStreak} day(s), best {r.LongestStreak}" + (r.CurrentStreak > 0 && !r.StudiedToday ? " [grey](study today to keep it)[/]" : ""));
        overview.AddRow("Answered today", r.AnsweredToday == 0 ? "0" : $"{r.AnsweredToday} ({Pct(r.CorrectToday, r.AnsweredToday)} right)");
        overview.AddRow("Accuracy, last 30 days", r.RecentAnswers == 0 ? "-" : $"{Pct(r.RecentCorrect, r.RecentAnswers)} of {r.RecentAnswers}");
        overview.AddRow("Learned", $"{r.Seen} / {r.QuestionsTotal} seen, {r.Typed} typed, {r.Mature} well learned (21+ days)");
        overview.AddRow("[bold]Due now[/]", $"[bold]{summary.DueReviews} reviews + {summary.NewAvailable} new[/]");
        overview.AddRow("Next 14 days", $"{r.Forecast.Sum(f => f.Due)} reviews: " + string.Join(" ", r.Forecast.Select(f => f.Due)));
        overview.AddRow("Next review", summary.NextDueAt is { } next ? TimeText.Until(next - now) : "-");
        AnsiConsole.Write(new Panel(overview).Header("Quizzler").Border(BoxBorder.Rounded));

        var topics = new Table().Border(TableBorder.Rounded)
            .AddColumn("Topic").AddColumn(new TableColumn("Seen").RightAligned())
            .AddColumn(new TableColumn("Well learned").RightAligned()).AddColumn(new TableColumn("Right, 30 days").RightAligned());
        foreach (var t in r.Topics)
            topics.AddRow(Markup.Escape(t.Topic), $"{t.Seen}/{t.Questions}", t.Mature.ToString(),
                t.RecentAnswers == 0 ? "[grey]-[/]" : $"{Pct(t.RecentCorrect, t.RecentAnswers)} of {t.RecentAnswers}");
        AnsiConsole.Write(topics);

        if (r.Retention.Any(b => b.Reviews > 0))
        {
            var memory = new Table().Border(TableBorder.Rounded).Title("Right after a gap of…")
                .AddColumn("Gap").AddColumn(new TableColumn("Right").RightAligned());
            foreach (var b in r.Retention.Where(b => b.Reviews > 0))
                memory.AddRow(b.Label, $"{Pct(b.Correct, b.Reviews)} of {b.Reviews}");
            AnsiConsole.Write(memory);
        }

        if (r.Leeches.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Questions you keep missing[/] [grey](3+ wrong, 40%+ of attempts, current wording)[/]");
            foreach (var l in r.Leeches)
            {
                var extra = l.Overrides > 0 ? $", overrode grader {l.Overrides}x" : "";
                var last = l.LastWrongResponse is { } w ? $" [grey]last wrong: {Markup.Escape(w.ReplaceLineEndings(" "))}[/]" : "";
                AnsiConsole.MarkupLine($"  [yellow]{l.Question.Id}[/] wrong {l.Wrong}/{l.Attempts}{extra}.{last}");
            }
        }

        var rem = r.Reminders;
        AnsiConsole.MarkupLine($"[grey]Reminders, last 30 days: {rem.Shown} shown, {rem.Started} opened a quiz, {rem.Snoozed} snoozed." +
                               (r.RecentOverrides > 0 ? $" Grader overridden {r.RecentOverrides}x." : "") + "[/]");

        var openFlags = db.GetFlags().Count;
        if (openFlags > 0)
            AnsiConsole.MarkupLine($"[yellow]{openFlags} flagged question(s).[/] See [yellow]quizzler flags[/].");

        return 0;
    }

    private static string Pct(int part, int whole) => whole == 0 ? "-" : $"{100 * part / whole}%";
}
