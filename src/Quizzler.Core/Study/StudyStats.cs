using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;

namespace Quizzler.Core.Study;

/// <summary>Answers on one study day (days roll over at <see cref="StudySettings.DayRolloverHour"/>, not midnight).</summary>
public sealed record DayActivity(DateOnly Day, int Answers, int Correct);

/// <summary>Cards coming due on one study day. Day 0 includes everything already overdue.</summary>
public sealed record ForecastDay(DateOnly Day, int Due);

/// <param name="Questions">Active questions in the topic.</param>
/// <param name="Seen">Questions answered at least once.</param>
/// <param name="Mature">Cards with an interval of 21+ days: well learned.</param>
/// <param name="RecentAnswers">Answers in the last 30 days.</param>
public sealed record TopicStats(string Topic, int Questions, int Seen, int Mature, int RecentAnswers, int RecentCorrect)
{
    public double? RecentAccuracy => RecentAnswers == 0 ? null : (double)RecentCorrect / RecentAnswers;
}

/// <summary>How often answers were right after the card had been left for a given gap: a forgetting curve.</summary>
public sealed record RetentionBucket(string Label, int Reviews, int Correct)
{
    public double? Rate => Reviews == 0 ? null : (double)Correct / Reviews;
}

/// <summary>A question the user keeps getting wrong (counted on its current wording only): it may need rewording.</summary>
public sealed record Leech(Question Question, int Wrong, int Attempts, int Overrides, string? LastWrongResponse);

/// <summary>Reminder outcomes over the last 30 days.</summary>
public sealed record ReminderStats(int Shown, int Started, int Snoozed);

public sealed record StatsReport(
    int CurrentStreak,
    int LongestStreak,
    bool StudiedToday,
    int AnsweredToday,
    int CorrectToday,
    int TotalAnswers,
    int RecentAnswers,
    int RecentCorrect,
    int QuestionsTotal,
    int Seen,
    int Typed,
    int Mature,
    TimeSpan? MedianAnswerTime,
    int RecentOverrides,
    IReadOnlyList<DayActivity> Days,
    IReadOnlyList<ForecastDay> Forecast,
    int NewPerDay,
    IReadOnlyList<TopicStats> Topics,
    IReadOnlyList<RetentionBucket> Retention,
    IReadOnlyList<Leech> Leeches,
    ReminderStats Reminders)
{
    public double? RecentAccuracy => RecentAnswers == 0 ? null : (double)RecentCorrect / RecentAnswers;
}

/// <summary>Turns cards, review history and usage events into the numbers the stats screens show. Pure, so it's unit-tested.</summary>
public static class StatsCalculator
{
    public const int RecentDays = 30;
    public const int MatureIntervalDays = 21;
    public const int ForecastDays = 14;
    public const int LeechMinWrong = 3;

    public static StatsReport Compute(
        IReadOnlyList<Question> questions,
        IReadOnlyList<CardState> cards,
        IReadOnlyList<ReviewLog> reviews,
        IReadOnlyList<StudyEventLog> events,
        StudySettings settings,
        DateTimeOffset now,
        TimeZoneInfo zone,
        IReadOnlyDictionary<string, string>? currentHashes = null,
        int heatmapDays = 140)
    {
        DateOnly DayOf(DateTimeOffset t)
        {
            var local = TimeZoneInfo.ConvertTime(t, zone);
            return DateOnly.FromDateTime(SessionPlanner.StartOfStudyDay(local, settings.DayRolloverHour).Date);
        }

        var today = DayOf(now);
        var recentStart = now.AddDays(-RecentDays);
        var activeIds = questions.Select(q => q.Id).ToHashSet();
        var activeCards = cards.Where(c => activeIds.Contains(c.QuestionId)).ToList();
        static bool IsCorrect(ReviewLog r) => r.Rating != Rating.Again;

        // ---- activity and streaks
        var byDay = reviews.GroupBy(r => DayOf(r.ReviewedAt)).ToDictionary(g => g.Key, g => (Answers: g.Count(), Correct: g.Count(IsCorrect)));
        var days = Enumerable.Range(0, heatmapDays)
            .Select(i => today.AddDays(i - heatmapDays + 1))
            .Select(d => byDay.TryGetValue(d, out var a) ? new DayActivity(d, a.Answers, a.Correct) : new DayActivity(d, 0, 0))
            .ToList();

        var studiedToday = byDay.ContainsKey(today);
        var current = 0;
        // A streak survives until the end of today even if today hasn't been studied yet.
        for (var d = studiedToday ? today : today.AddDays(-1); byDay.ContainsKey(d); d = d.AddDays(-1)) current++;

        int longest = 0, run = 0;
        DateOnly? previous = null;
        foreach (var d in byDay.Keys.Order())
        {
            run = previous is { } p && p.AddDays(1) == d ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = d;
        }

        // ---- forecast: due cards per study day
        var forecastCounts = new int[ForecastDays];
        foreach (var card in activeCards)
        {
            var offset = card.DueAt <= now ? 0 : DayOf(card.DueAt).DayNumber - today.DayNumber;
            if (offset < ForecastDays) forecastCounts[Math.Max(offset, 0)]++;
        }
        var forecast = forecastCounts.Select((n, i) => new ForecastDay(today.AddDays(i), n)).ToList();

        // ---- topics
        var recent = reviews.Where(r => r.ReviewedAt >= recentStart).ToList();
        var topicOf = questions.ToDictionary(q => q.Id, q => q.Topic);
        var cardsById = activeCards.ToDictionary(c => c.QuestionId);
        var topics = questions
            .GroupBy(q => q.Topic)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var ids = g.Select(q => q.Id).ToHashSet();
                var topicRecent = recent.Where(r => ids.Contains(r.QuestionId)).ToList();
                return new TopicStats(
                    g.Key,
                    ids.Count,
                    ids.Count(cardsById.ContainsKey),
                    ids.Count(id => cardsById.TryGetValue(id, out var c) && c.IntervalDays >= MatureIntervalDays),
                    topicRecent.Count,
                    topicRecent.Count(IsCorrect));
            })
            .ToList();

        // ---- forgetting curve, from reviews that know the gap they followed (recorded since schema v2)
        (string Label, double Min, double Max)[] buckets =
        [
            ("Same day (relearning)", 0.0001, 1),
            ("1 day", 1, 2),
            ("2–6 days", 2, 7),
            ("1–3 weeks", 7, 21),
            ("3+ weeks", 21, double.MaxValue),
        ];
        var retention = buckets
            .Select(b =>
            {
                var inBucket = reviews.Where(r => r.IntervalBeforeDays is { } gap && gap >= b.Min && gap < b.Max).ToList();
                return new RetentionBucket(b.Label, inBucket.Count, inBucket.Count(IsCorrect));
            })
            .ToList();

        // ---- leeches: wrong a lot on the question's current wording
        var questionsById = questions.ToDictionary(q => q.Id);
        bool IsCurrentVersion(ReviewLog r) =>
            r.QuestionHash is null || currentHashes is null || !currentHashes.TryGetValue(r.QuestionId, out var h) || h == r.QuestionHash;
        var leeches = reviews
            .Where(r => questionsById.ContainsKey(r.QuestionId) && IsCurrentVersion(r))
            .GroupBy(r => r.QuestionId)
            .Select(g =>
            {
                var wrong = g.Where(r => !IsCorrect(r)).ToList();
                return new Leech(questionsById[g.Key], wrong.Count, g.Count(), g.Count(r => r.WasOverridden), wrong.LastOrDefault()?.Response);
            })
            .Where(l => l.Wrong >= LeechMinWrong && l.Wrong * 10 >= l.Attempts * 4) // 3+ wrong and 40%+ of attempts
            .OrderByDescending(l => l.Wrong).ThenByDescending(l => (double)l.Wrong / l.Attempts).ThenBy(l => l.Question.Id, StringComparer.Ordinal)
            .Take(10)
            .ToList();

        // ---- reminders
        var recentEvents = events.Where(e => e.At >= recentStart).ToList();
        var reminders = new ReminderStats(
            recentEvents.Count(e => e.Kind == StudyEvent.ReminderShown),
            recentEvents.Count(e => e.Kind == StudyEvent.ReminderClicked && e.Detail?.StartsWith("snooze", StringComparison.Ordinal) != true),
            recentEvents.Count(e => e.Kind == StudyEvent.ReminderClicked && e.Detail?.StartsWith("snooze", StringComparison.Ordinal) == true));

        var recentTimes = recent.Select(r => r.Elapsed).Order().ToList();
        var todayReviews = reviews.Where(r => DayOf(r.ReviewedAt) == today).ToList();

        return new StatsReport(
            CurrentStreak: current,
            LongestStreak: longest,
            StudiedToday: studiedToday,
            AnsweredToday: todayReviews.Count,
            CorrectToday: todayReviews.Count(IsCorrect),
            TotalAnswers: reviews.Count,
            RecentAnswers: recent.Count,
            RecentCorrect: recent.Count(IsCorrect),
            QuestionsTotal: questions.Count,
            Seen: activeCards.Count,
            Typed: activeCards.Count(c => c.Stage == CardStage.Typed),
            Mature: activeCards.Count(c => c.IntervalDays >= MatureIntervalDays),
            MedianAnswerTime: recentTimes.Count == 0 ? null : recentTimes[recentTimes.Count / 2],
            RecentOverrides: recent.Count(r => r.WasOverridden),
            Days: days,
            Forecast: forecast,
            NewPerDay: settings.NewPerDay,
            Topics: topics,
            Retention: retention,
            Leeches: leeches,
            Reminders: reminders);
    }
}
