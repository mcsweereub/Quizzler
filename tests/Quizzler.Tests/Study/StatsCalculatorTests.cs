using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;

namespace Quizzler.Tests.Study;

public class StatsCalculatorTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(11);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test+11", Offset, "test", "test");

    // Thursday 2026-11-19, 9am. Study days roll over at 4am.
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 9, 0, 0, Offset);

    private static Question Q(string id, string topic = "csharp/oop") => new()
    {
        Id = id, Type = QuestionType.Concept, Topic = topic, Difficulty = 2, Prompt = "?", Answer = "a",
        Distractors = ["b", "c"], Explanation = "e",
    };

    private static ReviewLog R(string id, DateTimeOffset at, bool correct = true, double? gap = null, string? response = null, string? hash = null) =>
        new(id, at, CardStage.MultipleChoice, correct ? Rating.Good : Rating.Again, response, TimeSpan.FromSeconds(5))
        {
            IntervalBeforeDays = gap,
            QuestionHash = hash,
        };

    private static StatsReport Compute(
        IReadOnlyList<Question>? questions = null,
        IReadOnlyList<CardState>? cards = null,
        IReadOnlyList<ReviewLog>? reviews = null,
        IReadOnlyList<StudyEventLog>? events = null,
        IReadOnlyDictionary<string, string>? hashes = null) =>
        StatsCalculator.Compute(questions ?? [Q("q-0001")], cards ?? [], reviews ?? [], events ?? [], new StudySettings(), Now, Zone, hashes);

    [Fact]
    public void Empty_history_gives_zeroes_not_errors()
    {
        var report = Compute();
        Assert.Equal(0, report.CurrentStreak);
        Assert.Null(report.RecentAccuracy);
        Assert.Null(report.MedianAnswerTime);
        Assert.Equal(140, report.Days.Count);
        Assert.All(report.Retention, b => Assert.Null(b.Rate));
    }

    [Fact]
    public void Streak_counts_consecutive_study_days_and_survives_until_today_ends()
    {
        // Studied the last 3 days, but not yet today: the streak is still 3.
        var reviews = new[] { Now.AddDays(-1), Now.AddDays(-2), Now.AddDays(-3), Now.AddDays(-10), Now.AddDays(-11) }
            .Select(t => R("q-0001", t)).ToList();
        var report = Compute(reviews: reviews);

        Assert.False(report.StudiedToday);
        Assert.Equal(3, report.CurrentStreak);
        Assert.Equal(3, report.LongestStreak);
    }

    [Fact]
    public void Missing_a_whole_day_resets_the_streak()
    {
        var report = Compute(reviews: [R("q-0001", Now.AddDays(-2))]);
        Assert.Equal(0, report.CurrentStreak);
        Assert.Equal(1, report.LongestStreak);
    }

    [Fact]
    public void Late_night_study_counts_for_the_previous_day()
    {
        // 1am today is before the 4am rollover, so it belongs to yesterday: with yesterday evening that's one day.
        var oneAm = new DateTimeOffset(2026, 11, 19, 1, 0, 0, Offset);
        var report = Compute(reviews: [R("q-0001", oneAm), R("q-0001", oneAm.AddHours(-4))]);

        Assert.False(report.StudiedToday);
        Assert.Equal(1, report.LongestStreak);
        Assert.Equal(2, report.Days[^2].Answers);
    }

    [Fact]
    public void Utc_timestamps_are_grouped_by_local_study_day()
    {
        // 2026-11-18 20:00 UTC = 2026-11-19 07:00 at +11, which is today's study day.
        var utc = new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);
        var report = Compute(reviews: [R("q-0001", utc)]);
        Assert.True(report.StudiedToday);
        Assert.Equal(1, report.AnsweredToday);
    }

    [Fact]
    public void Forecast_puts_overdue_cards_in_today_and_ignores_retired_questions()
    {
        var cards = new[]
        {
            new CardState { QuestionId = "q-0001", DueAt = Now.AddDays(-3) },           // overdue → today
            new CardState { QuestionId = "q-0002", DueAt = Now.AddHours(2) },           // later today
            new CardState { QuestionId = "q-0003", DueAt = Now.AddDays(1) },            // tomorrow
            new CardState { QuestionId = "q-0004", DueAt = Now.AddDays(30) },           // beyond the window
            new CardState { QuestionId = "gone-0001", DueAt = Now },                    // question retired
        };
        var report = Compute(questions: [Q("q-0001"), Q("q-0002"), Q("q-0003"), Q("q-0004")], cards: cards);

        Assert.Equal(14, report.Forecast.Count);
        Assert.Equal(2, report.Forecast[0].Due);
        Assert.Equal(1, report.Forecast[1].Due);
        Assert.Equal(3, report.Forecast.Sum(f => f.Due));
    }

    [Fact]
    public void Topics_report_progress_and_recent_accuracy()
    {
        var questions = new[] { Q("a-0001", "csharp/oop"), Q("a-0002", "csharp/oop"), Q("b-0001", "sql/queries") };
        var cards = new[] { new CardState { QuestionId = "a-0001", IntervalDays = 30, DueAt = Now.AddDays(30) } };
        var reviews = new[] { R("a-0001", Now.AddDays(-1)), R("a-0001", Now.AddDays(-2), correct: false), R("a-0001", Now.AddDays(-60), correct: false) };
        var oop = Compute(questions, cards, reviews).Topics.Single(t => t.Topic == "csharp/oop");

        Assert.Equal(2, oop.Questions);
        Assert.Equal(1, oop.Seen);
        Assert.Equal(1, oop.Mature);
        Assert.Equal(2, oop.RecentAnswers); // the 60-day-old answer is outside the 30-day window
        Assert.Equal(0.5, oop.RecentAccuracy);
    }

    [Fact]
    public void Retention_buckets_use_the_gap_before_each_review()
    {
        var reviews = new[]
        {
            R("q-0001", Now, correct: true, gap: 1),
            R("q-0001", Now, correct: false, gap: 1),
            R("q-0001", Now, correct: true, gap: 30),
            R("q-0001", Now, correct: true, gap: 0),    // first sight of a new card: not retention
            R("q-0001", Now, correct: true, gap: null), // recorded before gaps were stored
        };
        var report = Compute(reviews: reviews);

        Assert.Equal(0.5, report.Retention.Single(b => b.Label == "1 day").Rate);
        Assert.Equal(1, report.Retention.Single(b => b.Label == "3+ weeks").Reviews);
        Assert.Equal(3, report.Retention.Sum(b => b.Reviews));
    }

    [Fact]
    public void Leeches_need_three_wrong_and_ignore_answers_to_an_older_wording()
    {
        var questions = new[] { Q("bad-0001"), Q("fixed-0001"), Q("ok-0001") };
        var reviews = new List<ReviewLog>();
        for (var i = 0; i < 3; i++) reviews.Add(R("bad-0001", Now.AddDays(-i), correct: false, response: $"try {i}", hash: "v1"));
        for (var i = 0; i < 4; i++) reviews.Add(R("fixed-0001", Now.AddDays(-i), correct: false, hash: "old"));
        for (var i = 0; i < 2; i++) reviews.Add(R("ok-0001", Now.AddDays(-i), correct: false, hash: "v1"));
        var hashes = new Dictionary<string, string> { ["bad-0001"] = "v1", ["fixed-0001"] = "new", ["ok-0001"] = "v1" };

        var leech = Assert.Single(Compute(questions, reviews: reviews, hashes: hashes).Leeches);
        Assert.Equal("bad-0001", leech.Question.Id);
        Assert.Equal(3, leech.Wrong);
        Assert.Equal("try 2", leech.LastWrongResponse);
    }

    [Fact]
    public void Mostly_right_questions_are_not_leeches()
    {
        var reviews = Enumerable.Range(0, 10).Select(i => R("q-0001", Now.AddDays(-i), correct: i >= 3)).ToList(); // 3 wrong of 10
        Assert.Empty(Compute(reviews: reviews).Leeches);
    }

    [Fact]
    public void Reminder_stats_separate_starts_from_snoozes()
    {
        var events = new[]
        {
            new StudyEventLog(Now.AddDays(-1), StudyEvent.ReminderShown, null),
            new StudyEventLog(Now.AddDays(-1), StudyEvent.ReminderClicked, "start"),
            new StudyEventLog(Now.AddDays(-2), StudyEvent.ReminderShown, null),
            new StudyEventLog(Now.AddDays(-2), StudyEvent.ReminderClicked, "snooze"),
            new StudyEventLog(Now.AddDays(-3), StudyEvent.ReminderClicked, "open (app was closed)"),
        };
        Assert.Equal(new ReminderStats(Shown: 2, Started: 2, Snoozed: 1), Compute(events: events).Reminders);
    }
}
