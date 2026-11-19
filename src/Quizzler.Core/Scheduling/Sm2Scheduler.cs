namespace Quizzler.Core.Scheduling;

public interface IScheduler
{
    /// <summary>Returns the card with interval, ease and due date updated for this rating. Stage is not touched.</summary>
    CardState Schedule(CardState card, Rating rating, DateTimeOffset now);
}

/// <summary>
/// A variant of the SuperMemo-2 algorithm (the one Anki was built on). Can be swapped for FSRS
/// later behind <see cref="IScheduler"/> without touching anything else.
/// </summary>
public sealed class Sm2Scheduler(TimeSpan? relearnDelay = null, double maxIntervalDays = 365) : IScheduler
{
    public const double DefaultEase = 2.5;
    public const double MinEase = 1.3;

    public TimeSpan RelearnDelay { get; } = relearnDelay ?? TimeSpan.FromMinutes(10);

    public CardState Schedule(CardState card, Rating rating, DateTimeOffset now)
    {
        var reviewed = card with
        {
            ReviewCount = card.ReviewCount + 1,
            FirstReviewedAt = card.ReviewCount == 0 ? now : card.FirstReviewedAt,
            LastReviewedAt = now,
        };

        if (rating == Rating.Again)
        {
            return reviewed with
            {
                Repetitions = 0,
                Lapses = card.Repetitions > 0 ? card.Lapses + 1 : card.Lapses,
                Ease = Math.Max(MinEase, card.Ease - 0.2),
                IntervalDays = 0,
                DueAt = now + RelearnDelay,
            };
        }

        var ease = rating switch
        {
            Rating.Hard => Math.Max(MinEase, card.Ease - 0.15),
            Rating.Easy => card.Ease + 0.15,
            _ => card.Ease,
        };

        var interval = (card.Repetitions, rating) switch
        {
            (0, Rating.Hard) => 1,
            (0, Rating.Good) => 1,
            (0, Rating.Easy) => 4,
            (1, Rating.Hard) => 3,
            (1, Rating.Good) => 6,
            (1, Rating.Easy) => 8,
            (_, Rating.Hard) => card.IntervalDays * 1.2,
            (_, Rating.Good) => card.IntervalDays * ease,
            (_, Rating.Easy) => card.IntervalDays * ease * 1.3,
            _ => throw new ArgumentOutOfRangeException(nameof(rating)),
        };

        // Whole days keep due dates tidy; a mature card's interval must always grow on success.
        interval = Math.Round(interval);
        if (card.Repetitions >= 2)
            interval = Math.Max(interval, Math.Ceiling(card.IntervalDays) + 1);
        interval = Math.Clamp(interval, 1, maxIntervalDays);

        return reviewed with
        {
            Repetitions = card.Repetitions + 1,
            Ease = ease,
            IntervalDays = interval,
            DueAt = now.AddDays(interval),
        };
    }
}
