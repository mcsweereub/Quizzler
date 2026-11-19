using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;
using Quizzler.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Quizzler.Cli.Commands;

public sealed class StatsCommand : Command<DbSettings>
{
    public override int Execute(CommandContext context, DbSettings settings, CancellationToken cancellationToken)
    {
        var db = QuizDatabase.Open(settings.ResolveDb());
        var service = new QuizService(db);
        var now = service.Now;

        var questions = db.GetActiveQuestions();
        if (questions.Count == 0)
        {
            AnsiConsole.MarkupLine("No questions yet. Run [yellow]quizzler import[/] first.");
            return 0;
        }

        var activeIds = questions.Select(q => q.Id).ToHashSet();
        var cards = db.GetCards().Where(c => activeIds.Contains(c.QuestionId)).ToDictionary(c => c.QuestionId);
        var summary = service.GetDueSummary();
        var dayStart = SessionPlanner.StartOfStudyDay(now, db.GetStudySettings().DayRolloverHour);
        var today = db.GetReviewsSince(dayStart);
        var correctToday = today.Count(r => r.Rating != Rating.Again);

        var overview = new Grid().AddColumn().AddColumn();
        overview.AddRow("Questions", $"{questions.Count}");
        overview.AddRow("Seen", $"{cards.Count}");
        overview.AddRow("At typed (recall) stage", $"{cards.Values.Count(c => c.Stage == CardStage.Typed)}");
        overview.AddRow("[bold]Due now[/]", $"[bold]{summary.DueReviews} reviews + {summary.NewAvailable} new[/]");
        overview.AddRow("Next review", summary.NextDueAt is { } next ? Humanize(next - now) : "-");
        overview.AddRow("Answered today", today.Count == 0 ? "0" : $"{today.Count} ({100 * correctToday / today.Count}% correct)");
        AnsiConsole.Write(new Panel(overview).Header("Quizzler").Border(BoxBorder.Rounded));

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Topic").AddColumn(new TableColumn("Total").RightAligned())
            .AddColumn(new TableColumn("Seen").RightAligned()).AddColumn(new TableColumn("Recall").RightAligned())
            .AddColumn(new TableColumn("Due").RightAligned());

        foreach (var topic in questions.GroupBy(q => q.Topic).OrderBy(g => g.Key))
        {
            var topicCards = topic.Select(q => cards.GetValueOrDefault(q.Id)).OfType<CardState>().ToList();
            table.AddRow(
                Markup.Escape(topic.Key),
                topic.Count().ToString(),
                topicCards.Count.ToString(),
                topicCards.Count(c => c.Stage == CardStage.Typed).ToString(),
                topicCards.Count(c => c.DueAt <= now).ToString());
        }
        AnsiConsole.Write(table);

        var openFlags = db.GetFlags().Count;
        if (openFlags > 0)
            AnsiConsole.MarkupLine($"[yellow]{openFlags} flagged question(s).[/] See [yellow]quizzler flags[/].");

        return 0;
    }

    internal static string Humanize(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "now",
        { TotalHours: < 1 } => $"in {(int)Math.Ceiling(span.TotalMinutes)} min",
        { TotalDays: < 1 } => $"in {(int)Math.Round(span.TotalHours)} h",
        _ => $"in {(int)Math.Round(span.TotalDays)} days",
    };
}
