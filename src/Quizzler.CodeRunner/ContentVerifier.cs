using System.Collections.Concurrent;
using Quizzler.Core.Grading;
using Quizzler.Core.Questions;

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
        var toRun = questions.Where(q => q.Question.Type == QuestionType.PredictOutput && !string.IsNullOrWhiteSpace(q.Question.Code));

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

    public static async Task<IReadOnlyList<string>> VerifyOneAsync(Question q)
    {
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

    private static string Indent(string s) => string.Join('\n', AnswerChecker.NormalizeOutput(s).Split('\n').Select(l => "    " + l));
}
