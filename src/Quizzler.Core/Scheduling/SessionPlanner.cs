using Quizzler.Core.Questions;

namespace Quizzler.Core.Scheduling;

public sealed record StudySettings
{
    /// <summary>Maximum brand-new questions introduced per study day.</summary>
    public int NewPerDay { get; init; } = 10;

    /// <summary>Local hour at which a new study day starts (so a late-night session counts as "today").</summary>
    public int DayRolloverHour { get; init; } = 4;
}

public sealed record SessionItem(Question Question, CardState Card, bool IsNew)
{
    public CardStage Stage => Card.Stage;
}

public sealed record SessionPlan(IReadOnlyList<SessionItem> Items, int DueReviews, int NewCards)
{
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>Decides what to ask right now: every due review, plus new questions up to today's limit.</summary>
public static class SessionPlanner
{
    /// <param name="filter">Limits the session to some kinds of question / one answer style. The new-per-day allowance
    /// is shared: new questions introduced in any session count against it.</param>
    public static SessionPlan Plan(
        IReadOnlyCollection<Question> questions,
        IReadOnlyCollection<CardState> cards,
        StudySettings settings,
        DateTimeOffset now,
        SessionFilter? filter = null)
    {
        filter ??= SessionFilter.Everything;
        var questionsById = questions.ToDictionary(q => q.Id);
        var dayStart = StartOfStudyDay(now, settings.DayRolloverHour);

        // Cards whose question has since been removed from content are ignored.
        var due = cards
            .Where(c => c.DueAt <= now && questionsById.TryGetValue(c.QuestionId, out var q)
                        && filter.Includes(q) && filter.AllowsStage(q, c.Stage, isNew: false))
            .OrderBy(c => c.DueAt)
            .Select(c => new SessionItem(questionsById[c.QuestionId], c, IsNew: false))
            .ToList();

        var introducedToday = cards.Count(c => c.FirstReviewedAt >= dayStart);
        var newAllowance = Math.Max(0, settings.NewPerDay - introducedToday);

        var seen = cards.Select(c => c.QuestionId).ToHashSet();
        var fresh = questions
            .Where(q => !seen.Contains(q.Id) && filter.Includes(q) && filter.AllowsStage(q, CardStage.MultipleChoice, isNew: true))
            .OrderBy(q => q.Difficulty)
            .ThenBy(q => q.Id, StringComparer.Ordinal)
            .Take(newAllowance)
            .Select(q => new SessionItem(q, ReviewEngine.NewCard(q.Id), IsNew: true))
            .ToList();

        return new SessionPlan([.. due, .. fresh], due.Count, fresh.Count);
    }

    public static DateTimeOffset StartOfStudyDay(DateTimeOffset now, int rolloverHour)
    {
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, rolloverHour, 0, 0, now.Offset);
        return now >= start ? start : start.AddDays(-1);
    }
}
