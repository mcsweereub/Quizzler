namespace Quizzler.Core.Scheduling;

/// <summary>One answered question. Kept forever: it drives stats and lets a future scheduler (e.g. FSRS) be fitted to real history.</summary>
public sealed record ReviewLog(
    string QuestionId,
    DateTimeOffset ReviewedAt,
    CardStage Stage,
    Rating Rating,
    string? Response,
    TimeSpan Elapsed);
