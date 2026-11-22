namespace Quizzler.Core.Questions;

/// <summary>The kinds of question a user can pick from when choosing what to study (finer than <see cref="QuestionType"/>).</summary>
public enum QuestionKind
{
    Concept,
    PredictOutput,
    PickTheCode,
    SqlResult,
    QueryPlan,
    CSharpExercise,
    SqlExercise,
}

public static class QuestionKinds
{
    public static IReadOnlyList<QuestionKind> All { get; } = Enum.GetValues<QuestionKind>();

    /// <summary>Kinds answered by multiple choice or typing (everything except coding exercises).</summary>
    public static IReadOnlyList<QuestionKind> Quiz { get; } = All.Where(k => !IsExercise(k)).ToList();

    public static IReadOnlyList<QuestionKind> Exercises { get; } = [QuestionKind.CSharpExercise, QuestionKind.SqlExercise];

    public static bool IsExercise(QuestionKind kind) => kind is QuestionKind.CSharpExercise or QuestionKind.SqlExercise;

    public static QuestionKind Of(Question q) => q.Type switch
    {
        QuestionType.Concept => QuestionKind.Concept,
        QuestionType.PredictOutput => QuestionKind.PredictOutput,
        QuestionType.PickTheCode => QuestionKind.PickTheCode,
        QuestionType.PredictResult when q.EffectiveExpect == ResultExpectation.TableAccess => QuestionKind.QueryPlan,
        QuestionType.PredictResult => QuestionKind.SqlResult,
        QuestionType.WriteCode when q.Language == ExerciseLanguage.Sql => QuestionKind.SqlExercise,
        QuestionType.WriteCode => QuestionKind.CSharpExercise,
        _ => throw new ArgumentOutOfRangeException(nameof(q), q.Type, null),
    };

    public static string Label(QuestionKind kind) => kind switch
    {
        QuestionKind.Concept => "Concepts",
        QuestionKind.PredictOutput => "C#: predict the output",
        QuestionKind.PickTheCode => "Pick the code",
        QuestionKind.SqlResult => "SQL: predict the result",
        QuestionKind.QueryPlan => "SQL: indexes and query plans",
        QuestionKind.CSharpExercise => "C# coding exercises",
        QuestionKind.SqlExercise => "SQL coding exercises",
        _ => kind.ToString(),
    };

    /// <summary>Command-line / settings name, e.g. "csharp-exercise".</summary>
    public static string Key(QuestionKind kind) => kind switch
    {
        QuestionKind.Concept => "concept",
        QuestionKind.PredictOutput => "predict-output",
        QuestionKind.PickTheCode => "pick-the-code",
        QuestionKind.SqlResult => "sql-result",
        QuestionKind.QueryPlan => "query-plan",
        QuestionKind.CSharpExercise => "csharp-exercise",
        QuestionKind.SqlExercise => "sql-exercise",
        _ => kind.ToString(),
    };

    public static QuestionKind? Parse(string key)
    {
        foreach (var kind in All)
        {
            if (string.Equals(Key(kind), key.Trim(), StringComparison.OrdinalIgnoreCase)) return kind;
        }
        return null;
    }
}
