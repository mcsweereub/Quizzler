namespace Quizzler.Core.Scheduling;

/// <summary>What the automatic grader decided, before the user refined it (guessed / too easy / override).</summary>
public enum ReviewGrade
{
    Correct,
    Incorrect,

    /// <summary>The answer was shown and the user marked themselves.</summary>
    SelfGraded,
}

/// <summary>One answered question. Kept forever: it drives stats and lets a future scheduler (e.g. FSRS) be fitted to real history.</summary>
public sealed record ReviewLog(
    string QuestionId,
    DateTimeOffset ReviewedAt,
    CardStage Stage,
    Rating Rating,
    string? Response,
    TimeSpan Elapsed)
{
    /// <summary>The grader's verdict. Incorrect with a non-Again <see cref="Rating"/> means the user overrode it.</summary>
    public ReviewGrade? Grade { get; init; }

    /// <summary>The card's interval before this review (0 for a new card): how long it had been left to be forgotten.</summary>
    public double? IntervalBeforeDays { get; init; }

    /// <summary>When the card was due before this review (null for a new card). Compare with ReviewedAt for lateness.</summary>
    public DateTimeOffset? DueBefore { get; init; }

    /// <summary>The interval the scheduler chose after this review.</summary>
    public double? IntervalAfterDays { get; init; }

    /// <summary>Which front end recorded it: "app" or "cli".</summary>
    public string? Client { get; init; }

    /// <summary>The question's content hash at the time (filled in by the store), so answers to an older wording can be told apart.</summary>
    public string? QuestionHash { get; init; }

    /// <summary>True when the user said "I was actually right" to a typed answer the grader rejected.</summary>
    public bool WasOverridden => Grade == ReviewGrade.Incorrect && Rating != Rating.Again;
}
