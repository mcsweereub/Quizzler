using Quizzler.Core.Questions;

namespace Quizzler.Core.Scheduling;

/// <summary>How quiz questions (not coding exercises) may be asked in a session.</summary>
public enum AnswerStyle
{
    /// <summary>As scheduled: new and relearning questions are multiple choice, learned ones are typed.</summary>
    Mixed,

    /// <summary>Only questions currently at the multiple-choice stage (including new ones).</summary>
    MultipleChoiceOnly,

    /// <summary>Only questions at the typed stage. No new questions.</summary>
    TypedOnly,
}

/// <summary>
/// Narrows a session to some kinds of question and/or one answer style. Questions are never asked in a different
/// style from the one they're scheduled for (that would undo the multiple choice → typed progression); a filtered-out
/// question just waits for a session that includes it.
/// </summary>
public sealed record SessionFilter
{
    public static SessionFilter Everything { get; } = new();

    /// <summary>Kinds to include; null or empty = all.</summary>
    public IReadOnlySet<QuestionKind>? Kinds { get; init; }

    public AnswerStyle Style { get; init; } = AnswerStyle.Mixed;

    private bool AllKinds => Kinds is null || Kinds.Count == 0 || Kinds.Count == QuestionKinds.All.Count;

    public bool IsEverything => AllKinds && Style == AnswerStyle.Mixed;

    public bool IncludesKind(QuestionKind kind) => Kinds is null || Kinds.Count == 0 || Kinds.Contains(kind);

    public bool Includes(Question question) => IncludesKind(QuestionKinds.Of(question));

    /// <summary>
    /// Whether a question at <paramref name="stage"/> (or brand new) fits the answer style. Exercises are neither
    /// multiple choice nor typed, so they only come with <see cref="AnswerStyle.Mixed"/>.
    /// </summary>
    public bool AllowsStage(Question question, CardStage stage, bool isNew)
    {
        if (question.IsExercise) return Style == AnswerStyle.Mixed;
        return Style switch
        {
            AnswerStyle.MultipleChoiceOnly => stage == CardStage.MultipleChoice,
            AnswerStyle.TypedOnly => !isNew && stage == CardStage.Typed,
            _ => true,
        };
    }

    /// <summary>Short description for the session header, e.g. "Multiple choice only · C# coding exercises".</summary>
    public string Describe()
    {
        if (IsEverything) return "Everything due";
        var parts = new List<string>();
        if (Style == AnswerStyle.MultipleChoiceOnly) parts.Add("Multiple choice only");
        if (Style == AnswerStyle.TypedOnly) parts.Add("Typed only");
        if (!AllKinds)
        {
            var kinds = Kinds!.Order().ToList();
            parts.Add(kinds.Count <= 2 ? string.Join(" + ", kinds.Select(QuestionKinds.Label)) : $"{kinds.Count} question types");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Compact form for settings / the command line: "style=mc;kinds=concept,sql-result".</summary>
    public string Serialize()
    {
        var style = Style switch { AnswerStyle.MultipleChoiceOnly => "mc", AnswerStyle.TypedOnly => "typed", _ => "mixed" };
        var kinds = AllKinds ? "all" : string.Join(',', Kinds!.Order().Select(QuestionKinds.Key));
        return $"style={style};kinds={kinds}";
    }

    public static SessionFilter Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Everything;
        var style = AnswerStyle.Mixed;
        HashSet<QuestionKind>? kinds = null;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2);
            var key = pieces[0].Trim();
            var value = pieces.Length > 1 ? pieces[1].Trim() : "";
            if (key == "style")
                style = ParseStyle(value) ?? AnswerStyle.Mixed;
            else if (key == "kinds" && value != "all")
                kinds = ParseKinds(value);
        }
        return new SessionFilter { Style = style, Kinds = kinds is { Count: > 0 } ? kinds : null };
    }

    /// <summary>Comma-separated kind keys; unknown ones are ignored.</summary>
    public static HashSet<QuestionKind> ParseKinds(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(QuestionKinds.Parse).OfType<QuestionKind>().ToHashSet();

    public static AnswerStyle? ParseStyle(string value) => value.Trim().ToLowerInvariant() switch
    {
        "mixed" or "all" or "" => AnswerStyle.Mixed,
        "mc" or "multiple-choice" or "choice" => AnswerStyle.MultipleChoiceOnly,
        "typed" or "type" or "recall" => AnswerStyle.TypedOnly,
        _ => null,
    };
}
