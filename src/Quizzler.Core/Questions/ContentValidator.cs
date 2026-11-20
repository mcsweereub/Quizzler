using System.Text.RegularExpressions;
using Quizzler.Core.Grading;

namespace Quizzler.Core.Questions;

/// <summary>
/// Structural and quality rules for authored questions. These are the rules the authoring guide
/// (CLAUDE.md) describes; keep the two in sync.
/// </summary>
public static partial class ContentValidator
{
    public const int MinDistractors = 2;
    public const int MaxDistractors = 5;
    public const int MaxExactConceptAnswerLength = 60;

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*-\d{4}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[a-z0-9-]+(/[a-z0-9-]+)*$")]
    private static partial Regex TopicPattern();

    [GeneratedRegex(@"^[a-z0-9#.+-]+$")]
    private static partial Regex TagPattern();

    public static IReadOnlyList<ContentIssue> Validate(IReadOnlyList<LoadedQuestion> questions)
    {
        var issues = new List<ContentIssue>();

        foreach (var group in questions.GroupBy(q => q.Question.Id).Where(g => g.Count() > 1))
        {
            var files = string.Join(", ", group.Select(g => g.SourceFile).Distinct());
            issues.Add(new(group.First().SourceFile, group.Key, $"Duplicate id (appears {group.Count()} times in: {files})."));
        }

        foreach (var (q, file) in questions)
        {
            void Issue(string message) => issues.Add(new(file, q.Id, message));

            if (!IdPattern().IsMatch(q.Id))
                Issue($"Id '{q.Id}' must look like 'cs-async-0007' (lowercase words, dash, 4-digit number).");

            if (!TopicPattern().IsMatch(q.Topic))
                Issue($"Topic '{q.Topic}' must be a lowercase slash path, e.g. 'csharp/async'.");

            if (q.Difficulty is < 1 or > 5)
                Issue($"Difficulty must be 1-5 (was {q.Difficulty}).");

            foreach (var tag in q.Tags.Where(t => !TagPattern().IsMatch(t)))
                Issue($"Tag '{tag}' must be lowercase with no spaces.");

            if (string.IsNullOrWhiteSpace(q.Prompt)) Issue("Prompt is required.");
            if (string.IsNullOrWhiteSpace(q.Answer)) Issue("Answer is required.");
            if (string.IsNullOrWhiteSpace(q.Explanation)) Issue("Explanation is required.");

            if (q.Type == QuestionType.PredictOutput && string.IsNullOrWhiteSpace(q.Code))
                Issue("predict-output questions require 'code'.");

            if (q.Type == QuestionType.PredictResult)
            {
                if (string.IsNullOrWhiteSpace(q.Code)) Issue("predict-result questions require 'code' (the SQL query).");
                if (string.IsNullOrWhiteSpace(q.Setup)) Issue("predict-result questions require 'setup' (SQL that creates the tables).");
            }
            else if (q.Setup is not null || q.Expect is not null)
            {
                Issue("'setup' and 'expect' are only for predict-result questions.");
            }

            if (q.Distractors.Count is < MinDistractors or > MaxDistractors)
                Issue($"Needs {MinDistractors}-{MaxDistractors} distractors (has {q.Distractors.Count}).");

            if (q.Distractors.Any(string.IsNullOrWhiteSpace) || q.Accepted.Any(string.IsNullOrWhiteSpace))
                Issue("Distractors and accepted answers must not be blank.");

            // A distractor that the grader would accept as correct makes the question unanswerable.
            var correctForms = q.Accepted.Prepend(q.Answer).ToList();
            foreach (var d in q.Distractors)
            {
                if (correctForms.Any(c => AnswerChecker.Matches(q.Type, c, d)))
                    Issue($"Distractor '{Shorten(d)}' matches a correct answer.");
            }

            if (q.Distractors.Count != q.Distractors.Distinct().Count())
                Issue("Distractors must be unique.");

            if (q.Type == QuestionType.Concept
                && q.EffectiveRecall == RecallGrading.Exact
                && q.Answer.Length > MaxExactConceptAnswerLength)
            {
                Issue($"Concept answer is {q.Answer.Length} chars; exact-graded answers must be <= {MaxExactConceptAnswerLength}. " +
                      "Shorten it or set \"recall\": \"self\".");
            }
        }

        return issues;
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..37] + "...";
}
