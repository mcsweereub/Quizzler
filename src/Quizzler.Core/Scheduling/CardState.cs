namespace Quizzler.Core.Scheduling;

/// <summary>How the question is presented next time.</summary>
public enum CardStage
{
    /// <summary>Recognition: pick the answer from options. Every question starts here.</summary>
    MultipleChoice,

    /// <summary>Recall: type the answer with no options shown.</summary>
    Typed,
}

public enum Rating
{
    /// <summary>Wrong. Relearn soon.</summary>
    Again,

    /// <summary>Right, but guessed or struggled.</summary>
    Hard,

    /// <summary>Right.</summary>
    Good,

    /// <summary>Right, trivially.</summary>
    Easy,
}

/// <summary>A user's progress on one question. Questions with no CardState are "new".</summary>
public sealed record CardState
{
    public required string QuestionId { get; init; }
    public CardStage Stage { get; init; } = CardStage.MultipleChoice;

    /// <summary>Consecutive successful reviews since the last lapse.</summary>
    public int Repetitions { get; init; }

    /// <summary>SM-2 ease factor; interval multiplier for successful reviews.</summary>
    public double Ease { get; init; } = Sm2Scheduler.DefaultEase;

    public double IntervalDays { get; init; }
    public DateTimeOffset DueAt { get; init; }
    public int Lapses { get; init; }
    public int ReviewCount { get; init; }
    public DateTimeOffset FirstReviewedAt { get; init; }
    public DateTimeOffset LastReviewedAt { get; init; }
}
