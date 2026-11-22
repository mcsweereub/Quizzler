using System.Collections.Concurrent;
using Quizzler.Core.Grading;
using Quizzler.Core.Questions;
using Quizzler.Data;

namespace Quizzler.CodeRunner;

/// <summary>
/// Executes every predict-output question and checks its answer key against what the code really prints.
/// Snippets compile in parallel; execution is serialised inside <see cref="CSharpRunner"/>.
/// </summary>
public static class ContentVerifier
{
    public static async Task<IReadOnlyList<ContentIssue>> VerifyAsync(
        IEnumerable<LoadedQuestion> questions,
        IProgress<LoadedQuestion>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new ConcurrentBag<ContentIssue>();
        var toRun = questions.Where(q => IsRunnable(q.Question));

        await Parallel.ForEachAsync(
            toRun,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (loaded, _) =>
            {
                foreach (var message in await VerifyOneAsync(loaded.Question))
                    issues.Add(new ContentIssue(loaded.SourceFile, loaded.Question.Id, message));
                progress?.Report(loaded);
            });

        return issues.OrderBy(i => i.SourceFile).ThenBy(i => i.QuestionId).ToList();
    }

    /// <summary>Questions whose answer key can be checked by running them: C# snippets and SQL queries.</summary>
    public static bool IsRunnable(Question q) =>
        q.Type switch
        {
            QuestionType.PredictOutput => !string.IsNullOrWhiteSpace(q.Code),
            QuestionType.PredictResult => !string.IsNullOrWhiteSpace(q.Code) && !string.IsNullOrWhiteSpace(q.Setup),
            QuestionType.WriteCode => q.Language is not null,
            _ => false,
        };

    public static async Task<IReadOnlyList<string>> VerifyOneAsync(Question q)
    {
        if (q.Type == QuestionType.PredictResult) return VerifySql(q);
        if (q.Type == QuestionType.WriteCode) return await VerifyExerciseAsync(q);

        var result = await CSharpRunner.RunAsync(q.Code!);

        if (!result.Compiled)
            return ["Code does not compile:\n  " + string.Join("\n  ", result.Errors)];
        if (result.TimedOut)
            return ["Code timed out (infinite loop, or waiting on input?)."];
        if (result.Errors.Count > 0)
            return ["Code threw at runtime (catch and print instead): " + string.Join("; ", result.Errors)];

        var messages = new List<string>();
        if (!AnswerChecker.Matches(QuestionType.PredictOutput, q.Answer, result.Output))
        {
            messages.Add($"Answer key is wrong. Expected:\n{Indent(q.Answer)}\n  but the code printed:\n{Indent(AnswerChecker.NormalizeOutput(result.Output))}");
        }

        foreach (var d in q.Distractors.Where(d => AnswerChecker.Matches(QuestionType.PredictOutput, d, result.Output)))
            messages.Add($"Distractor matches the real output: {d.ReplaceLineEndings("\\n")}");

        return messages;
    }

    /// <summary>Runs the setup + query in in-memory SQLite and checks the answer key and distractors against the real result.</summary>
    public static IReadOnlyList<string> VerifySql(Question q)
    {
        SqlRun run;
        try
        {
            run = SqlSandbox.Run(q, analyseRows: true);
        }
        catch (SqlRunException ex)
        {
            return [$"SQL error (SQLite dialect): {ex.Message}"];
        }

        var messages = new List<string>();
        string actual;
        try
        {
            actual = run.AnswerFor(q);
        }
        catch (SqlRunException ex)
        {
            return [ex.Message];
        }
        if (!AnswerChecker.Matches(QuestionType.PredictResult, q.Answer, actual))
            messages.Add($"Answer key is wrong. Expected:\n{IndentResult(q.Answer)}\n  but the query returned:\n{IndentResult(actual)}");

        foreach (var d in q.Distractors.Where(d => AnswerChecker.Matches(QuestionType.PredictResult, d, actual)))
            messages.Add($"Distractor matches the real result: {d.ReplaceLineEndings("\\n")}");

        // Without ORDER BY, SQL doesn't promise any row order, so a multi-row answer key would be luck.
        if (q.EffectiveExpect == ResultExpectation.Rows && run.Result.Rows.Count > 1
            && !q.Code!.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add("The query returns several rows but has no ORDER BY, so the row order isn't guaranteed. Add ORDER BY.");
        }

        if (run.Tables.Count == 0)
            messages.Add("Setup creates no tables to show.");

        return messages;
    }

    /// <summary>The reference solution (the answer) must pass every test; for SQL, it must run and return rows.</summary>
    public static async Task<IReadOnlyList<string>> VerifyExerciseAsync(Question q)
    {
        ExerciseResult result;
        try
        {
            result = await ExerciseRunner.RunAsync(q, q.Answer);
        }
        catch (SqlRunException ex)
        {
            return [$"SQL error in the setup or reference query (SQLite dialect): {ex.Message}"];
        }

        var messages = new List<string>();
        if (!result.AllPassed)
            messages.Add("The reference solution ('answer') doesn't pass:\n  " + result.Describe().ReplaceLineEndings("\n  "));

        if (q.Language == ExerciseLanguage.Sql && result.Sql is { } sql)
        {
            if (sql.Expected.Rows.Count == 0) messages.Add("The reference query returns no rows, so any empty query would pass. Change the data or the query.");
            if (sql.Tables.Count == 0) messages.Add("Setup creates no tables to show.");
        }

        // A starter that already passes means the exercise asks for nothing.
        if (!string.IsNullOrWhiteSpace(q.Starter) && (await ExerciseRunner.RunAsync(q, q.Starter)).AllPassed)
            messages.Add("The starter code already passes every test.");

        return messages;
    }

    private static string IndentResult(string s) => string.Join('\n', s.ReplaceLineEndings("\n").Split('\n').Select(l => "    " + l));

    private static string Indent(string s) => string.Join('\n', AnswerChecker.NormalizeOutput(s).Split('\n').Select(l => "    " + l));
}
