using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Quizzler.Ai;
using Quizzler.CodeRunner;
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

        [CommandOption("--types <LIST>")]
        [Description("Only these kinds, comma-separated: concept, predict-output, pick-the-code, sql-result, query-plan, csharp-exercise, sql-exercise.")]
        public string? Types { get; set; }

        [CommandOption("--style <STYLE>")]
        [Description("mixed (default), mc (only questions at the multiple-choice stage, incl. new) or typed (only learned ones). Coding exercises only come with mixed.")]
        public string? Style { get; set; }

        public override ValidationResult Validate()
        {
            if (Style is not null && SessionFilter.ParseStyle(Style) is null)
                return ValidationResult.Error("--style must be mixed, mc or typed.");
            if (Types is not null)
            {
                var bad = Types.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(t => QuestionKinds.Parse(t) is null).ToList();
                if (bad.Count > 0)
                    return ValidationResult.Error($"Unknown type(s): {string.Join(", ", bad)}. Use: {string.Join(", ", QuestionKinds.All.Select(QuestionKinds.Key))}.");
            }
            return ValidationResult.Success();
        }

        public SessionFilter Filter => new()
        {
            Style = Style is null ? AnswerStyle.Mixed : SessionFilter.ParseStyle(Style)!.Value,
            Kinds = Types is null ? null : SessionFilter.ParseKinds(Types),
        };
    }

    private enum Next { Continue, Quit }

    private QuizDatabase _db = null!;

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var db = _db = QuizDatabase.Open(settings.ResolveDb());
        var service = new QuizService(db) { Client = "cli" };
        var filter = settings.Filter;
        var plan = service.PlanSession(filter);

        if (db.GetActiveQuestions().Count == 0)
        {
            AnsiConsole.MarkupLine("No questions yet. Run [yellow]quizzler import[/] first.");
            return 0;
        }

        if (plan.IsEmpty)
        {
            var summary = service.GetDueSummary();
            if (!filter.IsEverything && summary.Total > 0)
            {
                AnsiConsole.MarkupLine($"Nothing ready for [bold]{Markup.Escape(filter.Describe())}[/], but {summary.Total} other question(s) are. Run [yellow]quizzler quiz[/] without filters.");
                return 0;
            }
            var next = summary.NextDueAt is { } at ? TimeText.Until(at - service.Now) : "when you add more questions";
            AnsiConsole.MarkupLine($"[green]All caught up![/] Next review {next}.");
            return 0;
        }

        var items = plan.Items.Take(settings.Limit ?? int.MaxValue).ToList();
        var filterText = filter.IsEverything ? "" : $" [grey]({Markup.Escape(filter.Describe())})[/]";
        AnsiConsole.MarkupLine($"[bold]{plan.DueReviews}[/] reviews and [bold]{plan.NewCards}[/] new questions{filterText}. " +
                               "[grey]Type q at any answer prompt to stop.[/]");

        int answered = 0, correct = 0;
        var random = new Random();
        var detail = $"{plan.DueReviews} reviews, {plan.NewCards} new" + (filter.IsEverything ? "" : $"; filter {filter.Serialize()}");
        service.LogEvent(StudyEvent.SessionStarted, detail);

        for (var i = 0; i < items.Count; i++)
        {
            AnsiConsole.WriteLine();
            var (next, wasCorrect) = items[i].Question.IsExercise
                ? AskExercise(items[i], i + 1, items.Count, service)
                : Ask(items[i], i + 1, items.Count, service, random);
            if (next == Next.Quit) break;
            answered++;
            if (wasCorrect) correct++;
        }
        service.LogEvent(StudyEvent.SessionEnded, $"{answered}/{items.Count} answered, {correct} correct");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Session done[/]").LeftJustified());
        if (answered > 0)
            AnsiConsole.MarkupLine($"Answered {answered}, correct {correct} ({100 * correct / answered}%).");

        var after = service.GetDueSummary();
        if (after.NextDueAt is { } nextDue)
            AnsiConsole.MarkupLine($"Next review {TimeText.Until(nextDue - service.Now)}.");
        return 0;
    }

    private (Next Next, bool Correct) Ask(SessionItem item, int index, int total, QuizService service, Random random)
    {
        var q = item.Question;
        var mode = item.Stage == CardStage.MultipleChoice ? "multiple choice" : "type your answer";
        var badge = item.IsNew ? " [aqua]NEW[/]" : "";
        AnsiConsole.Write(new Rule($"[grey]{index}/{total} · {Markup.Escape(q.Topic)} · difficulty {q.Difficulty} · {mode}[/]{badge}").LeftJustified());

        AnsiConsole.MarkupLine(Format(q.Prompt));

        SqlRun? sql = null;
        if (q.Type == QuestionType.PredictResult)
        {
            try
            {
                sql = SqlSandbox.Run(q, analyseRows: true);
                foreach (var table in sql.Tables) AnsiConsole.Write(SqlGrid(table, null, table.Name));
            }
            catch (SqlRunException ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            }
        }

        if (!string.IsNullOrWhiteSpace(q.Code))
            AnsiConsole.Write(CodePanel(q.Code));

        var stopwatch = Stopwatch.StartNew();
        bool isCorrect;
        ReviewGrade grade;
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
            grade = isCorrect ? ReviewGrade.Correct : ReviewGrade.Incorrect;
            AnsiConsole.MarkupLine(isCorrect ? "[green]✔ Correct[/]" : $"[red]✘ Incorrect.[/] The answer was [bold]{mc.CorrectIndex + 1}[/].");
        }
        else
        {
            var isRowCount = q.Type == QuestionType.PredictResult && q.EffectiveExpect == ResultExpectation.RowCount;
            var isPlan = q.Type == QuestionType.PredictResult && q.EffectiveExpect == ResultExpectation.TableAccess;
            var multiLine = q.Type != QuestionType.Concept && !isRowCount && !isPlan;
            response = multiLine
                ? ReadMultiLine(q.Type switch
                {
                    QuestionType.PredictOutput => "Type the exact output",
                    QuestionType.PredictResult => "Type the result rows, values separated by commas, NULL for nulls",
                    _ => "Write your answer",
                })
                : ReadLine(isRowCount ? "Number of rows: " : isPlan ? "How is the table read (e.g. Full table scan, Index seek)? " : "Your answer: ");
            if (response is null || IsQuit(response)) return (Next.Quit, false);

            var result = AnswerChecker.Check(q, response);
            grade = result switch
            {
                CheckResult.Correct => ReviewGrade.Correct,
                CheckResult.Incorrect => ReviewGrade.Incorrect,
                _ => ReviewGrade.SelfGraded,
            };
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

        if (sql is not null && q.EffectiveExpect == ResultExpectation.TableAccess)
        {
            var plan = string.Join('\n', sql.Plan.Select(s => new string(' ', s.Depth * 3) + s.Detail));
            AnsiConsole.Write(CodePanel(plan, "Query plan (SQLite)"));
            AnsiConsole.MarkupLine("[grey]SCAN reads every row; SEARCH jumps to matching rows via the index; COVERING means no trip back to the table.[/]");
        }
        else if (sql is not null)
        {
            var count = sql.Result.Rows.Count == 1 ? "1 row" : $"{sql.Result.Rows.Count} rows";
            AnsiConsole.Write(SqlGrid(sql.Result, null, $"Result · {count}"));
            AnsiConsole.MarkupLine("[grey]Rows that affect the result (deleting one would change it) are bold; the rest are dimmed:[/]");
            for (var t = 0; t < sql.Tables.Count; t++)
                AnsiConsole.Write(SqlGrid(sql.Tables[t], sql.RowMatters?[t], sql.Tables[t].Name));
        }

        AnsiConsole.MarkupLine($"[grey]{Format(q.Explanation)}[/]");

        var outcome = FollowUp(q, service, isCorrect, canOverride, explain: isCorrect ? null : new MistakeRequest(q, response ?? "", item.Stage == CardStage.MultipleChoice));
        if (outcome == AnswerOutcome.Right && !isCorrect) isCorrect = true; // overridden
        var updated = service.RecordAnswer(item, outcome, response, stopwatch.Elapsed, grade);
        var nextIn = TimeText.Until(updated.DueAt - service.Now);
        var nextMode = updated.Stage == CardStage.Typed ? "typed" : "multiple choice";
        AnsiConsole.MarkupLine($"[grey]Next time: {nextMode}, {nextIn}.[/]");

        return (Next.Continue, isCorrect);
    }

    /// <summary>After an answer: refine the rating, override a too-strict grader, ask Claude why, or flag a bad question.</summary>
    private AnswerOutcome FollowUp(Question q, QuizService service, bool isCorrect, bool canOverride, MistakeRequest? explain, bool struggled = false)
    {
        var parts = new List<string> { "[[Enter]] next" };
        if (isCorrect && !q.IsExercise) parts.Add("[[g]] I guessed");
        if (isCorrect) parts.Add("[[e]] too easy");
        if (canOverride) parts.Add("[[o]] I was actually right");
        if (explain is not null) parts.Add("[[x]] ask Claude what I got wrong");
        parts.Add("[[f]] flag question");
        AnsiConsole.MarkupLine($"[grey]{string.Join(" · ", parts)}[/]");

        var outcome = !isCorrect ? AnswerOutcome.Wrong : struggled ? AnswerOutcome.Guessed : AnswerOutcome.Right;
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
            if (key == "x" && explain is not null)
            {
                Explain(service, explain);
                continue;
            }
            if (isCorrect && key == "g") { outcome = AnswerOutcome.Guessed; break; }
            if (isCorrect && key == "e") { outcome = AnswerOutcome.Easy; break; }
            if (canOverride && key == "o") { outcome = AnswerOutcome.Right; break; }
            if (IsQuit(key)) break; // still record this answer, then stop
        }
        return outcome;
    }

    /// <summary>Streams Claude's explanation of the mistake to the terminal.</summary>
    private void Explain(QuizService service, MistakeRequest request)
    {
        var key = new ApiKeyStore(_db.GetSetting, _db.SetOrDeleteSetting).GetKey();
        if (key is null)
        {
            AnsiConsole.MarkupLine("[yellow]No Anthropic API key.[/] Set [aqua]ANTHROPIC_API_KEY[/], or add one in the app's Settings.");
            return;
        }

        service.LogEvent(StudyEvent.ExplanationRequested, request.Question.Id);
        AnsiConsole.MarkupLine("[bold]Claude:[/]");
        try
        {
            var stream = new ClaudeMistakeExplainer(key).ExplainAsync(request).GetAsyncEnumerator();
            try
            {
                while (stream.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                    Console.Write(stream.Current);
            }
            finally
            {
                stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Console.WriteLine();
        }
        catch (ExplainException ex)
        {
            AnsiConsole.MarkupLine($"\n[red]{Markup.Escape(ex.Message)}[/]");
            service.LogEvent(StudyEvent.ExplanationRequested, $"{request.Question.Id} failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ coding exercises

    /// <summary>
    /// Coding exercise in the terminal: the code lives in a file you edit with any editor; Enter runs the tests.
    /// The file is kept as a draft (also shared with the app) until you pass or give up.
    /// </summary>
    private (Next Next, bool Correct) AskExercise(SessionItem item, int index, int total, QuizService service)
    {
        var q = item.Question;
        var isSql = q.Language == ExerciseLanguage.Sql;
        var badge = item.IsNew ? " [aqua]NEW[/]" : "";
        AnsiConsole.Write(new Rule($"[grey]{index}/{total} · {Markup.Escape(q.Topic)} · difficulty {q.Difficulty} · coding exercise[/]{badge}").LeftJustified());
        AnsiConsole.MarkupLine(Format(q.Prompt));

        if (isSql)
        {
            try
            {
                foreach (var table in SqlSandbox.Run(q.Setup ?? "", q.Answer).Tables) AnsiConsole.Write(SqlGrid(table, null, table.Name));
            }
            catch (SqlRunException ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            }
        }
        if (!string.IsNullOrWhiteSpace(q.Helpers)) AnsiConsole.Write(CodePanel(q.Helpers, "Provided"));
        foreach (var (t, i) in q.Tests.Select((t, i) => (t, i)))
            AnsiConsole.MarkupLine($"  [grey]{i + 1}.[/] [aqua]{Markup.Escape(t.Call)}[/] [grey]→ {Markup.Escape(t.Expect)}[/]");

        var dir = Path.Combine(Path.GetTempPath(), "Quizzler", "exercises");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, q.Id + (isSql ? ".sql" : ".cs"));
        File.WriteAllText(file, _db.GetSetting($"draft:{q.Id}") ?? q.Starter ?? "");
        AnsiConsole.MarkupLine($"Write your {(isSql ? "query" : "code")} in [link]{Markup.Escape(file)}[/]");
        AnsiConsole.MarkupLine("[grey][[Enter]] run · [[e]] open in editor (QUIZZLER_EDITOR, EDITOR or Notepad) · [[s]] give up and see a solution · [[q]] stop (code is kept)[/]");

        var stopwatch = Stopwatch.StartNew();
        var failedRuns = 0;
        ExerciseResult? last = null;
        bool passed;
        while (true)
        {
            var key = (ReadLine("> ") ?? "q").Trim().ToLowerInvariant();
            if (IsQuit(key))
            {
                SaveDraft(q, File.ReadAllText(file));
                return (Next.Quit, false);
            }
            if (key == "e")
            {
                OpenEditor(file);
                continue;
            }
            if (key == "s")
            {
                passed = false;
                break;
            }

            var code = File.ReadAllText(file);
            last = ExerciseRunner.RunAsync(q, code).GetAwaiter().GetResult();
            PrintRun(last);
            service.LogEvent(StudyEvent.ExerciseRun, last.Status == ExerciseStatus.CompileError
                ? $"{q.Id}: compile error"
                : $"{q.Id}: {last.PassedCount}/{last.TotalCount} passed ({last.Status.ToString().ToLowerInvariant()})");
            if (last.AllPassed)
            {
                passed = true;
                break;
            }
            if (last.Status != ExerciseStatus.CompileError) failedRuns++;
        }
        stopwatch.Stop();

        var response = File.ReadAllText(file);
        _db.DeleteSetting($"draft:{q.Id}");
        AnsiConsole.MarkupLine(passed
            ? failedRuns == 0 ? "[green]✔ All tests pass, first time.[/]" : $"[green]✔ All tests pass[/] [grey](after {failedRuns} failed run(s), so it comes back a little sooner).[/]"
            : "[yellow]A reference solution:[/]");
        if (!passed) AnsiConsole.Write(CodePanel(q.Answer).BorderColor(Color.Green));
        AnsiConsole.MarkupLine($"[grey]{Format(q.Explanation)}[/]");

        var request = passed ? null : new MistakeRequest(q, response, false, last?.Describe() ?? "They gave up before running the code.");
        var outcome = FollowUp(q, service, passed, canOverride: false, request, struggled: failedRuns > 0);
        var updated = service.RecordAnswer(item, outcome, response, stopwatch.Elapsed, passed ? ReviewGrade.Correct : ReviewGrade.Incorrect);
        AnsiConsole.MarkupLine($"[grey]Next time: {TimeText.Until(updated.DueAt - service.Now)}.[/]");
        return (Next.Continue, passed);
    }

    private void SaveDraft(Question q, string code) =>
        _db.SetOrDeleteSetting($"draft:{q.Id}", string.IsNullOrWhiteSpace(code) || code.Trim() == (q.Starter ?? "").Trim() ? null : code);

    private static void PrintRun(ExerciseResult result)
    {
        var colour = result.AllPassed ? "green" : "red";
        AnsiConsole.MarkupLine($"[{colour}]{Markup.Escape(result.Summary)}[/]");
        foreach (var e in result.CompileErrors) AnsiConsole.MarkupLine($"  [red]{Markup.Escape(e.ToString())}[/]");
        foreach (var t in result.Tests.Where(t => t.Ran || t.Error is "timed out" or "crashed"))
        {
            var detail = t.Passed ? $"→ {t.Actual}" : t.Actual is not null ? $"expected {t.Expected}, got {t.Actual}" : $"expected {t.Expected}: {t.Error}";
            AnsiConsole.MarkupLine($"  {(t.Passed ? "[green]✔[/]" : "[red]✘[/]")} [aqua]{Markup.Escape(t.Call)}[/] [grey]{Markup.Escape(detail)}[/]");
        }
        // The expected rows only after a real attempt (a query that ran), never for an empty or broken one.
        if (result.Sql is { Actual: { } actual } sql)
        {
            AnsiConsole.Write(SqlGrid(actual, null, "Your result"));
            if (!sql.Passed) AnsiConsole.Write(SqlGrid(sql.Expected, null, "Expected"));
        }
        if (!string.IsNullOrWhiteSpace(result.Output)) AnsiConsole.Write(CodePanel(result.Output.TrimEnd(), "Your code printed"));
    }

    private static void OpenEditor(string file)
    {
        var editor = Environment.GetEnvironmentVariable("QUIZZLER_EDITOR") ?? Environment.GetEnvironmentVariable("EDITOR") ?? "notepad";
        try
        {
            // "code --wait" style values: first word is the program, the rest are arguments.
            var parts = editor.Split(' ', 2);
            var start = new ProcessStartInfo(parts[0]) { UseShellExecute = parts[0].EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) };
            if (parts.Length > 1) foreach (var arg in parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)) start.ArgumentList.Add(arg);
            start.ArgumentList.Add(file);
            AnsiConsole.MarkupLine("[grey]Waiting for the editor to close…[/]");
            Process.Start(start)?.WaitForExit();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[red]Couldn't start '{Markup.Escape(editor)}': {Markup.Escape(ex.Message)}[/] Edit the file yourself, then press Enter.");
        }
    }

    private static void ShowAnswer(Question q)
    {
        if (q.Type == QuestionType.Concept && !q.Answer.Contains('\n'))
            AnsiConsole.MarkupLine($"  [bold green]{Format(q.Answer)}[/]");
        else
            AnsiConsole.Write(CodePanel(q.Answer).BorderColor(Color.Green));
    }

    private static Table SqlGrid(SqlTable table, IReadOnlyList<bool>? rowMatters, string title)
    {
        var grid = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title($"[grey]{Markup.Escape(title)}[/]");
        foreach (var column in table.Columns) grid.AddColumn($"[bold]{Markup.Escape(column)}[/]");
        if (table.Rows.Count == 0) grid.AddRow(table.Columns.Select((_, i) => i == 0 ? "[grey italic](no rows)[/]" : "").ToArray());
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var style = rowMatters is null ? "" : rowMatters[r] ? "bold" : "grey";
            grid.AddRow(table.Rows[r].Select(value =>
            {
                var cell = value == SqlSandbox.Null ? "[italic]NULL[/]" : Markup.Escape(value);
                return style == "" ? cell : $"[{style}]{cell}[/]";
            }).ToArray());
        }
        if (table.IndexList.Count > 0)
            grid.Caption($"[grey]{Markup.Escape(string.Join("\n", table.IndexList))}[/]");
        return grid;
    }

    private static Panel CodePanel(string code, string? header = null)
    {
        var panel = new Panel(new Text(code)).Border(BoxBorder.Rounded).BorderColor(Color.Grey).Padding(1, 0);
        return header is null ? panel : panel.Header($" {header} ");
    }

    /// <summary>Escapes Spectre markup, highlights `inline code` and renders **bold**.</summary>
    private static string Format(string text) =>
        Bold().Replace(InlineCode().Replace(Markup.Escape(text), "[aqua]$1[/]"), "[bold]$1[/]");

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*(?=\S)([^*\n]+?)(?<=\S)\*\*")]
    private static partial Regex Bold();

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
