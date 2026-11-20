using System.Text.RegularExpressions;
using Quizzler.Core.Questions;

namespace Quizzler.Core.Grading;

public enum CheckResult
{
    Correct,
    Incorrect,

    /// <summary>The question is self-graded; show the answer and let the user decide.</summary>
    NeedsSelfGrade,
}

/// <summary>Grades typed (recall-stage) answers.</summary>
public static partial class AnswerChecker
{
    public static CheckResult Check(Question question, string typed)
    {
        if (question.EffectiveRecall == RecallGrading.Self)
            return CheckResult.NeedsSelfGrade;

        var correct = question.Accepted.Prepend(question.Answer).Any(c => Matches(question.Type, c, typed));
        return correct ? CheckResult.Correct : CheckResult.Incorrect;
    }

    /// <summary>True when <paramref name="candidate"/> should be accepted as <paramref name="expected"/>.</summary>
    public static bool Matches(QuestionType type, string expected, string candidate) => type switch
    {
        QuestionType.PredictOutput => NormalizeOutput(expected) == NormalizeOutput(candidate),
        QuestionType.PredictResult => NormalizeResult(expected) == NormalizeResult(candidate),
        _ => NormalizeText(expected) == NormalizeText(candidate),
    };

    /// <summary>
    /// SQL result comparison: one row per line, values separated by commas (or |). Case-insensitive,
    /// ignores spacing around values, quotes around a value ('Ada' = Ada), and blank lines.
    /// </summary>
    public static string NormalizeResult(string s)
    {
        var rows = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => string.Join(", ", line.Split(',', '|').Select(NormalizeCell)));
        return string.Join('\n', rows);

        static string NormalizeCell(string cell)
        {
            var c = Whitespace().Replace(cell.Trim(), " ").ToLowerInvariant();
            return c.Length >= 2 && c[0] == c[^1] && c[0] is '\'' or '"' ? c[1..^1] : c;
        }
    }

    /// <summary>
    /// Program output comparison: case-sensitive (True vs true matters in C#), but forgiving about
    /// line endings, trailing spaces on each line, and leading/trailing blank lines.
    /// </summary>
    public static string NormalizeOutput(string s)
    {
        var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd());
        return string.Join('\n', lines).Trim('\n');
    }

    /// <summary>
    /// Free-text comparison: case-insensitive, collapses whitespace, ignores surrounding quotes or
    /// backticks, a trailing full stop, and a trailing "()" (so "GetHashCode()" matches "GetHashCode").
    /// </summary>
    public static string NormalizeText(string s)
    {
        var t = Whitespace().Replace(s.Trim(), " ").ToLowerInvariant();
        t = t.Trim('`', '"', '\'').TrimEnd('.').Trim();
        if (t.EndsWith("()", StringComparison.Ordinal)) t = t[..^2];
        return t;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
