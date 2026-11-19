using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Quizzler.Core.Grading;
using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;
using Quizzler.Data;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Quizzler.Cli.Commands;

/// <summary>
/// Terminal study session. Uses plain Console.ReadLine for input (not Spectre prompts) so it also
/// works with redirected stdin, which keeps it scriptable for smoke tests.
/// </summary>
public sealed partial class QuizCommand : Command<QuizCommand.Settings>
{
    public sealed class Settings : DbSettings
    {
        [CommandOption("--limit <N>")]
        [Description("Stop after this many questions.")]
        public int? Limit { get; set; }
    }

    private enum Next { Continue, Quit }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var db = QuizDatabase.Open(settings.ResolveDb());
        var service = new QuizService(db);
        var plan = service.PlanSession();

        if (db.GetActiveQuestions().Count == 0)
        {
            AnsiConsole.MarkupLine("No questions yet. Run [yellow]quizzler import[/] first.");
            return 0;
        }

        if (plan.IsEmpty)
        {
            var summary = service.GetDueSummary();
            var next = summary.NextDueAt is { } at ? TimeText.Until(at - service.Now) : "when you add more questions";
            AnsiConsole.MarkupLine($"[green]All caught up![/] Next review {next}.");
            return 0;
        }

        var items = plan.Items.Take(settings.Limit ?? int.MaxValue).ToList();
        AnsiConsole.MarkupLine($"[bold]{plan.DueReviews}[/] reviews and [bold]{plan.NewCards}[/] new questions. " +
                               "[grey]Type q at any answer prompt to stop.[/]");

        int answered = 0, correct = 0;
        var random = new Random();

        for (var i = 0; i < items.Count; i++)
        {
            AnsiConsole.WriteLine();
            var (next, wasCorrect) = Ask(items[i], i + 1, items.Count, service, random);
            if (next == Next.Quit) break;
            answered++;
            if (wasCorrect) correct++;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Session done[/]").LeftJustified());
        if (answered > 0)
            AnsiConsole.MarkupLine($"Answered {answered}, correct {correct} ({100 * correct / answered}%).");

        var after = service.GetDueSummary();
        if (after.NextDueAt is { } nextDue)
            AnsiConsole.MarkupLine($"Next review {TimeText.Until(nextDue - service.Now)}.");
        return 0;
    }

    private static (Next Next, bool Correct) Ask(SessionItem item, int index, int total, QuizService service, Random random)
    {
        var q = item.Question;
        var mode = item.Stage == CardStage.MultipleChoice ? "multiple choice" : "type your answer";
        var badge = item.IsNew ? " [aqua]NEW[/]" : "";
        AnsiConsole.Write(new Rule($"[grey]{index}/{total} · {Markup.Escape(q.Topic)} · difficulty {q.Difficulty} · {mode}[/]{badge}").LeftJustified());

        AnsiConsole.MarkupLine(Format(q.Prompt));
        if (!string.IsNullOrWhiteSpace(q.Code))
            AnsiConsole.Write(CodePanel(q.Code));

        var stopwatch = Stopwatch.StartNew();
        bool isCorrect;
        string? response;
        bool canOverride = false;

        if (item.Stage == CardStage.MultipleChoice)
        {
            var mc = MultipleChoice.Build(q, random);
            for (var o = 0; o < mc.Options.Count; o++)
            {
                var option = mc.Options[o];
                if (q.Type == QuestionType.PickTheCode || option.Contains('\n'))
                    AnsiConsole.Write(CodePanel(option, $"{o + 1}"));
                else
                    AnsiConsole.MarkupLine($"  [bold]{o + 1})[/] {Format(option)}");
            }

            int choice;
            while (true)
            {
                var input = ReadLine($"Your answer (1-{mc.Options.Count}): ");
                if (IsQuit(input)) return (Next.Quit, false);
                if (int.TryParse(input, out choice) && choice >= 1 && choice <= mc.Options.Count) break;
            }

            response = mc.Options[choice - 1];
            isCorrect = mc.IsCorrect(choice - 1);
            AnsiConsole.MarkupLine(isCorrect ? "[green]✔ Correct[/]" : $"[red]✘ Incorrect.[/] The answer was [bold]{mc.CorrectIndex + 1}[/].");
        }
        else
        {
            var multiLine = q.Type != QuestionType.Concept;
            response = multiLine
                ? ReadMultiLine(q.Type == QuestionType.PredictOutput ? "Type the exact output" : "Write your answer")
                : ReadLine("Your answer: ");
            if (response is null || IsQuit(response)) return (Next.Quit, false);

            var result = AnswerChecker.Check(q, response);
            if (result == CheckResult.NeedsSelfGrade)
            {
                AnsiConsole.MarkupLine("[bold]Answer:[/]");
                ShowAnswer(q);
                isCorrect = AskYesNo("Did you get it right?");
            }
            else
            {
                isCorrect = result == CheckResult.Correct;
                canOverride = !isCorrect;
                if (isCorrect)
                {
                    AnsiConsole.MarkupLine("[green]✔ Correct[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]✘ Incorrect.[/] The answer is:");
                    ShowAnswer(q);
                }
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine($"[grey]{Format(q.Explanation)}[/]");

        // Follow-up: refine the rating, override a too-strict grader, or flag a bad question.
        var hints = isCorrect
            ? "[grey][[Enter]] next · [[g]] I guessed · [[e]] too easy · [[f]] flag question[/]"
            : canOverride
                ? "[grey][[Enter]] next · [[o]] I was actually right · [[f]] flag question[/]"
                : "[grey][[Enter]] next · [[f]] flag question[/]";
        AnsiConsole.MarkupLine(hints);

        var outcome = isCorrect ? AnswerOutcome.Right : AnswerOutcome.Wrong;
        while (true)
        {
            var key = (ReadLine("> ") ?? "").Trim().ToLowerInvariant();
            if (key == "") break;
            if (key == "f")
            {
                var note = ReadLine("What's wrong with it? (optional): ");
                service.Flag(q.Id, note);
                AnsiConsole.MarkupLine("[yellow]Flagged.[/] [grey]Press Enter to continue.[/]");
                continue;
            }
            if (isCorrect && key == "g") { outcome = AnswerOutcome.Guessed; break; }
            if (isCorrect && key == "e") { outcome = AnswerOutcome.Easy; break; }
            if (canOverride && key == "o") { outcome = AnswerOutcome.Right; isCorrect = true; break; }
            if (IsQuit(key)) break; // still record this answer, then stop
        }

        var updated = service.RecordAnswer(item, outcome, response, stopwatch.Elapsed);
        var nextIn = TimeText.Until(updated.DueAt - service.Now);
        var nextMode = updated.Stage == CardStage.Typed ? "typed" : "multiple choice";
        AnsiConsole.MarkupLine($"[grey]Next time: {nextMode}, {nextIn}.[/]");

        return (Next.Continue, isCorrect);
    }

    private static void ShowAnswer(Question q)
    {
        if (q.Type == QuestionType.Concept && !q.Answer.Contains('\n'))
            AnsiConsole.MarkupLine($"  [bold green]{Format(q.Answer)}[/]");
        else
            AnsiConsole.Write(CodePanel(q.Answer).BorderColor(Color.Green));
    }

    private static Panel CodePanel(string code, string? header = null)
    {
        var panel = new Panel(new Text(code)).Border(BoxBorder.Rounded).BorderColor(Color.Grey).Padding(1, 0);
        return header is null ? panel : panel.Header($" {header} ");
    }

    /// <summary>Escapes Spectre markup and highlights `inline code`.</summary>
    private static string Format(string text) => InlineCode().Replace(Markup.Escape(text), "[aqua]$1[/]");

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex InlineCode();

    private static bool IsQuit(string? input) => input?.Trim().ToLowerInvariant() is "q" or "quit";

    private static string? ReadLine(string prompt)
    {
        AnsiConsole.Markup($"[bold]{Markup.Escape(prompt)}[/]");
        return ReadRawLine();
    }

    /// <summary>Console.ReadLine, minus the byte-order mark some shells prepend to piped input.</summary>
    private static string? ReadRawLine() => Console.ReadLine()?.TrimStart('﻿');

    private static string? ReadMultiLine(string prompt)
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(prompt)}[/] [grey](finish with an empty line)[/]");
        var lines = new List<string>();
        while (true)
        {
            var line = ReadRawLine();
            if (line is null) return lines.Count == 0 ? null : string.Join('\n', lines); // end of input
            if (line.Length == 0) return string.Join('\n', lines);
            lines.Add(line);
        }
    }

    private static bool AskYesNo(string question)
    {
        while (true)
        {
            var input = (ReadLine($"{question} (y/n): ") ?? "n").Trim().ToLowerInvariant();
            if (input is "y" or "yes") return true;
            if (input is "n" or "no") return false;
        }
    }
}
