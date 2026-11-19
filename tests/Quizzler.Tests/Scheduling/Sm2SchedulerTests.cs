using Quizzler.Core.Scheduling;

namespace Quizzler.Tests.Scheduling;

public class Sm2SchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.FromHours(11));
    private readonly Sm2Scheduler _scheduler = new();

    private CardState ReviewTimes(params Rating[] ratings)
    {
        var card = ReviewEngine.NewCard("q-0001");
        var now = Now;
        foreach (var r in ratings)
        {
            card = _scheduler.Schedule(card, r, now);
            now = card.DueAt;
        }
        return card;
    }

    [Fact]
    public void Good_reviews_grow_1_then_6_then_by_ease()
    {
        Assert.Equal(1, ReviewTimes(Rating.Good).IntervalDays);
        Assert.Equal(6, ReviewTimes(Rating.Good, Rating.Good).IntervalDays);
        Assert.Equal(15, ReviewTimes(Rating.Good, Rating.Good, Rating.Good).IntervalDays);
    }

    [Fact]
    public void Due_date_is_now_plus_interval()
    {
        var card = _scheduler.Schedule(ReviewEngine.NewCard("q-0001"), Rating.Good, Now);
        Assert.Equal(Now.AddDays(1), card.DueAt);
    }

    [Fact]
    public void Again_relearns_in_ten_minutes_and_resets_repetitions()
    {
        var mature = ReviewTimes(Rating.Good, Rating.Good, Rating.Good);
        var lapsed = _scheduler.Schedule(mature, Rating.Again, Now);

        Assert.Equal(Now.AddMinutes(10), lapsed.DueAt);
        Assert.Equal(0, lapsed.Repetitions);
        Assert.Equal(1, lapsed.Lapses);
        Assert.Equal(mature.Ease - 0.2, lapsed.Ease, precision: 6);
    }

    [Fact]
    public void Failing_a_brand_new_card_is_not_a_lapse()
    {
        Assert.Equal(0, ReviewTimes(Rating.Again).Lapses);
    }

    [Fact]
    public void Ease_never_drops_below_minimum()
    {
        var card = ReviewTimes(Enumerable.Repeat(Rating.Again, 20).ToArray());
        Assert.Equal(Sm2Scheduler.MinEase, card.Ease, precision: 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void Hard_is_shorter_than_good_which_is_shorter_than_easy(int priorGoodReviews)
    {
        var card = ReviewTimes(Enumerable.Repeat(Rating.Good, priorGoodReviews).ToArray());

        var hard = _scheduler.Schedule(card, Rating.Hard, Now).IntervalDays;
        var good = _scheduler.Schedule(card, Rating.Good, Now).IntervalDays;
        var easy = _scheduler.Schedule(card, Rating.Easy, Now).IntervalDays;

        Assert.True(hard <= good && good < easy, $"hard={hard} good={good} easy={easy}");
    }

    [Fact]
    public void Mature_card_interval_always_grows_even_on_hard()
    {
        var card = ReviewTimes(Enumerable.Repeat(Rating.Hard, 10).ToArray());
        var next = _scheduler.Schedule(card, Rating.Hard, Now);
        Assert.True(next.IntervalDays > card.IntervalDays);
    }

    [Fact]
    public void Interval_is_capped()
    {
        var card = ReviewTimes(Enumerable.Repeat(Rating.Easy, 15).ToArray());
        Assert.Equal(365, card.IntervalDays);
    }

    [Fact]
    public void Tracks_review_counts_and_timestamps()
    {
        var first = _scheduler.Schedule(ReviewEngine.NewCard("q-0001"), Rating.Good, Now);
        var second = _scheduler.Schedule(first, Rating.Good, Now.AddDays(1));

        Assert.Equal(2, second.ReviewCount);
        Assert.Equal(Now, second.FirstReviewedAt);
        Assert.Equal(Now.AddDays(1), second.LastReviewedAt);
    }
}
