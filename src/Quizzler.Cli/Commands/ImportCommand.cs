using Quizzler.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Quizzler.Cli.Commands;

public sealed class ImportCommand : AsyncCommand<ContentSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ContentSettings settings, CancellationToken cancellationToken)
    {
        var check = await ContentCheck.RunAsync(settings, cancellationToken);
        if (check is not { } ok) return 1;
        if (ok.Issues.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Not importing until the issues above are fixed.[/]");
            return 1;
        }

        var dbPath = settings.ResolveDb();
        var db = QuizDatabase.Open(dbPath);
        var report = db.ImportQuestions(ok.Load.Questions, DateTimeOffset.Now);

        AnsiConsole.MarkupLine($"Database: [grey]{Markup.Escape(dbPath)}[/]");
        AnsiConsole.MarkupLine(
            $"[green]Imported.[/] {report.Added.Count} added, {report.Updated.Count} updated, " +
            $"{report.Restored.Count} restored, {report.Retired.Count} retired, {report.Unchanged} unchanged.");

        if (report.Retired.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Retired (no longer in content; progress kept in case they return):[/] {Markup.Escape(string.Join(", ", report.Retired))}");
        }

        return 0;
    }
}
