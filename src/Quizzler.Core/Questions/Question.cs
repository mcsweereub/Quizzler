namespace Quizzler.Core.Questions;

public enum QuestionType
{
    /// <summary>Conceptual question with a short factual answer.</summary>
    Concept,

    /// <summary>A code snippet is shown; the answer is exactly what it prints.</summary>
    PredictOutput,

    /// <summary>The answer and distractors are code snippets; pick the one that solves the prompt.</summary>
    PickTheCode,

    /// <summary>
    /// SQL: <see cref="Question.Setup"/> builds small tables, <see cref="Question.Code"/> is a query, and the answer is
    /// its result (rows, or the row count when <see cref="Question.Expect"/> is <see cref="ResultExpectation.RowCount"/>).
    /// </summary>
    PredictResult,
}

/// <summary>What a predict-result answer describes.</summary>
public enum ResultExpectation
{
    /// <summary>The result rows: one per line, values separated by commas.</summary>
    Rows,

    /// <summary>Just the number of rows returned.</summary>
    RowCount,

    /// <summary>
    /// How the database reads a table, from its query plan: one of <see cref="TableAccessNames.All"/>
    /// (e.g. "Index seek", "Full table scan"). See <see cref="Question.PlanTable"/>.
    /// </summary>
    TableAccess,
}

/// <summary>The vocabulary for <see cref="ResultExpectation.TableAccess"/> answers.</summary>
public static class TableAccessNames
{
    public const string FullTableScan = "Full table scan";
    public const string FullIndexScan = "Full index scan";
    public const string CoveringIndexScan = "Covering index scan";
    public const string IndexSeek = "Index seek";
    public const string CoveringIndexSeek = "Covering index seek";
    public const string PrimaryKeySeek = "Primary key seek";

    public static IReadOnlyList<string> All { get; } =
        [FullTableScan, FullIndexScan, CoveringIndexScan, IndexSeek, CoveringIndexSeek, PrimaryKeySeek];
}

/// <summary>How a typed (recall-stage) answer is graded.</summary>
public enum RecallGrading
{
    /// <summary>Typed text is compared against <see cref="Question.Answer"/> and <see cref="Question.Accepted"/>.</summary>
    Exact,

    /// <summary>The user types an attempt, sees the real answer, and marks themselves right or wrong.</summary>
    Self,
}

/// <summary>
/// A question as authored in the content/ JSON files. Content is immutable reference data;
/// the user's progress on a question lives separately in <see cref="Scheduling.CardState"/>.
/// </summary>
public sealed record Question
{
    /// <summary>Stable, unique id, e.g. "cs-async-0007". Never change it once published: progress is keyed on it.</summary>
    public string Id { get; init; } = "";

    public QuestionType Type { get; init; }

    /// <summary>Slash-separated topic path, e.g. "csharp/async".</summary>
    public string Topic { get; init; } = "";

    /// <summary>1 (junior fundamentals) to 5 (expert / obscure edge cases).</summary>
    public int Difficulty { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string Prompt { get; init; } = "";

    /// <summary>Optional code shown with the prompt: C#, or the SQL query for predict-result. Required for predict-output and predict-result.</summary>
    public string? Code { get; init; }

    /// <summary>predict-result only: SQL that creates and fills the tables the query runs against.</summary>
    public string? Setup { get; init; }

    /// <summary>predict-result only: whether the answer is the rows (default) or the row count.</summary>
    public ResultExpectation? Expect { get; init; }

    public ResultExpectation EffectiveExpect => Expect ?? ResultExpectation.Rows;

    /// <summary>
    /// table-access only: which table's access the question is about, as named in the plan (its alias if the query
    /// gives it one). Optional when the query reads a single table.
    /// </summary>
    public string? PlanTable { get; init; }

    /// <summary>The canonical correct answer. Shown as the right option in multiple-choice mode.</summary>
    public string Answer { get; init; } = "";

    /// <summary>Alternative typed answers also accepted as correct in recall mode.</summary>
    public IReadOnlyList<string> Accepted { get; init; } = [];

    /// <summary>Plausible wrong answers used as the other options in multiple-choice mode.</summary>
    public IReadOnlyList<string> Distractors { get; init; } = [];

    /// <summary>Why the answer is right (and ideally why the tempting wrong answers are wrong).</summary>
    public string Explanation { get; init; } = "";

    /// <summary>Overrides the default recall grading for this question type.</summary>
    public RecallGrading? Recall { get; init; }

    public RecallGrading EffectiveRecall =>
        Recall ?? (Type == QuestionType.PickTheCode ? RecallGrading.Self : RecallGrading.Exact);
}
