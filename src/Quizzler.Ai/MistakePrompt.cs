using System.Text;
using Quizzler.Core.Questions;

namespace Quizzler.Ai;

/// <summary>What the user got wrong, as sent to Claude.</summary>
/// <param name="UserAnswer">Their answer: the option they picked, what they typed, or their code.</param>
/// <param name="WasMultipleChoice">They picked from options (so the answer is a distractor, often a known misconception).</param>
/// <param name="RunReport">Exercises: the test/result report from running their code.</param>
public sealed record MistakeRequest(Question Question, string UserAnswer, bool WasMultipleChoice, string? RunReport = null);

/// <summary>Builds the "explain my mistake" prompt. Pure, so it's unit-tested without calling the API.</summary>
public static class MistakePrompt
{
    public const string System = """
        You are a patient senior engineer tutoring someone who studies for software engineering work and interviews
        (C#, .NET, SQL, design, algorithms) with spaced-repetition flashcards. They just got a card wrong.

        Your job: work out the specific misconception that most likely produced *their* answer, and correct it.
        - Start with the misconception itself, addressed to them ("You're treating... as..."). No greeting, and don't restate the question.
        - Say concretely why their answer is wrong and why the right answer is right. For code, trace what actually happens;
          for SQL, point at the rows involved.
        - If one tiny example would make it click, give it inline with `backticks`.
        - For a coding exercise, point at the bug or the failing case and the idea needed to fix it. Don't write the whole solution.
        - If their answer is actually correct or equivalent (the text grader can be strict), say that first and plainly.
        - Under 140 words. Plain sentences only: no headings, lists, bold or code blocks. Use `backticks` for code.
        """;

    public static string BuildUserMessage(MistakeRequest r)
    {
        var q = r.Question;
        var sb = new StringBuilder();
        sb.Append($"<card topic=\"{q.Topic}\" type=\"{KindName(q)}\" difficulty=\"{q.Difficulty}\">\n");
        Tag(sb, "question", q.Prompt);
        if (!string.IsNullOrWhiteSpace(q.Setup)) Tag(sb, "sql_setup", q.Setup);
        if (!string.IsNullOrWhiteSpace(q.Code)) Tag(sb, q.Type == QuestionType.PredictResult ? "sql_query" : "code", q.Code);
        if (!string.IsNullOrWhiteSpace(q.Helpers)) Tag(sb, "provided_helpers", q.Helpers);
        if (q.Tests.Count > 0) Tag(sb, "tests", string.Join('\n', q.Tests.Select(t => $"{t.Call}  =>  {t.Expect}")));
        Tag(sb, q.IsExercise ? "reference_solution" : "correct_answer", q.Answer);
        if (!string.IsNullOrWhiteSpace(q.Explanation)) Tag(sb, "card_explanation", q.Explanation);
        sb.Append("</card>\n\n");

        var how = q.IsExercise ? "Their code" : r.WasMultipleChoice ? "They picked this option" : "They typed";
        sb.Append($"<their_answer how=\"{how}\">\n{r.UserAnswer.Trim()}\n</their_answer>\n");
        if (!string.IsNullOrWhiteSpace(r.RunReport)) Tag(sb, "run_result", r.RunReport);
        sb.Append("\nExplain their mistake.");
        return sb.ToString();
    }

    private static string KindName(Question q) => QuestionKinds.Label(QuestionKinds.Of(q));

    private static void Tag(StringBuilder sb, string name, string content) =>
        sb.Append('<').Append(name).Append(">\n").Append(content.Trim()).Append("\n</").Append(name).Append(">\n");
}
