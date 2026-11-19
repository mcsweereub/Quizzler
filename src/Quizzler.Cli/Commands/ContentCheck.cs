using Quizzler.CodeRunner;
using Quizzler.Core.Questions;
using Spectre.Console;

namespace Quizzler.Cli.Commands;

/// <summary>Shared by validate and import: load, validate, optionally execute, and print a report.</summary>
internal static class ContentCheck
{
    public static async Task<(ContentLoadResult Load, IReadOnlyList<ContentIssue> Issues)?> RunAsync(
        ContentSettings settings, CancellationToken cancellationToken)
    {
        var dir = settings.ResolveContent();
        if (dir is null)
        {
            AnsiConsole.MarkupLine("[red]Couldn't find a content folder.[/] Run from the repo, or pass [yellow]--content <DIR>[/].");
            return null;
        }

        AnsiConsole.MarkupLine($"Content: [grey]{Markup.Escape(dir)}[/]");
        var load = ContentLoader.LoadDirectory(dir);
        var issues = load.Issues.ToList();

        var byType = load.Questions.GroupBy(q => q.Question.Type).ToDictionary(g => g.Key, g => g.Count());
        AnsiConsole.MarkupLine(
            $"Loaded [bold]{load.Questions.Count}[/] questions " +
            $"([grey]{byType.GetValueOrDefault(QuestionType.Concept)} concept, " +
            $"{byType.GetValueOrDefault(QuestionType.PredictOutput)} predict-output, " +
            $"{byType.GetValueOrDefault(QuestionType.PickTheCode)} pick-the-code[/])");

        var toRun = byType.GetValueOrDefault(QuestionType.PredictOutput);
        if (!settings.NoRun && toRun > 0)
        {
            var verifyIssues = await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"Running {toRun} snippets", maxValue: toRun);
                    var progress = new Progress<LoadedQuestion>(_ => task.Increment(1));
                    var result = await ContentVerifier.VerifyAsync(load.Questions, progress, cancellationToken);
                    task.Value = toRun;
                    return result;
                });
            issues.AddRange(verifyIssues);
        }

        if (issues.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✔ No issues.[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded).AddColumns("File", "Question", "Problem");
            foreach (var issue in issues)
                table.AddRow(Markup.Escape(issue.SourceFile), Markup.Escape(issue.QuestionId ?? "-"), Markup.Escape(issue.Message));
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[red]✘ {issues.Count} issue(s).[/]");
        }

        return (load, issues);
    }
}
