using Quizzler.Core.Scheduling;

namespace Quizzler.Core.Study;

/// <summary>What the user did with a question, before it's turned into a scheduler rating.</summary>
public enum AnswerOutcome
{
    Wrong,

    /// <summary>Right, but the user says they guessed.</summary>
    Guessed,

    Right,

    /// <summary>Right, and too easy. Pushes the next review further out.</summary>
    Easy,
}

/// <summary>Counts for the tray icon / notifications.</summary>
public sealed record DueSummary(int DueReviews, int NewAvailable, DateTimeOffset? NextDueAt)
{
    public int Total => DueReviews + NewAvailable;
}

/// <summary>
/// The study loop shared by every front end (terminal quiz now, WPF app later):
/// plan what's due, record answers, report what's coming up.
/// </summary>
public sealed class QuizService(IQuizStore store, ReviewEngine engine, TimeProvider clock)
{
    public QuizService(IQuizStore store) : this(store, new ReviewEngine(new Sm2Scheduler()), TimeProvider.System) { }

    public DateTimeOffset Now => clock.GetLocalNow();

    /// <summary>Recorded with every answer and event: "app" or "cli".</summary>
    public string Client { get; init; } = "unknown";

    public IQuizStore Store => store;

    public void LogEvent(string kind, string? detail = null)
    {
        try
        {
            store.LogEvent(kind, detail, Now);
        }
        catch (Exception)
        {
            // Usage events are nice-to-have; never let one break studying.
        }
    }

    public SessionPlan PlanSession(SessionFilter? filter = null) =>
        SessionPlanner.Plan(store.GetActiveQuestions(), store.GetCards(), store.GetStudySettings(), Now, filter);

    /// <summary>How many questions each filter would give right now (one database read for all of them).</summary>
    public IReadOnlyList<SessionPlan> PlanSessions(IEnumerable<SessionFilter> filters)
    {
        var questions = store.GetActiveQuestions();
        var cards = store.GetCards();
        var settings = store.GetStudySettings();
        var now = Now;
        return filters.Select(f => SessionPlanner.Plan(questions, cards, settings, now, f)).ToList();
    }

    public DueSummary GetDueSummary()
    {
        var now = Now;
        var questions = store.GetActiveQuestions();
        var cards = store.GetCards();
        var plan = SessionPlanner.Plan(questions, cards, store.GetStudySettings(), now);
        var activeIds = questions.Select(q => q.Id).ToHashSet();
        var next = cards
            .Where(c => c.DueAt > now && activeIds.Contains(c.QuestionId))
            .Select(c => (DateTimeOffset?)c.DueAt)
            .Min();
        return new DueSummary(plan.DueReviews, plan.NewCards, next);
    }

    public static Rating ToRating(AnswerOutcome outcome) => outcome switch
    {
        AnswerOutcome.Wrong => Rating.Again,
        AnswerOutcome.Guessed => Rating.Hard,
        AnswerOutcome.Right => Rating.Good,
        AnswerOutcome.Easy => Rating.Easy,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    /// <summary>Applies the answer to the card, persists it, and returns the updated card.</summary>
    public CardState RecordAnswer(SessionItem item, AnswerOutcome outcome, string? response, TimeSpan elapsed, ReviewGrade? grade = null)
    {
        var now = Now;
        var rating = ToRating(outcome);
        var updated = engine.Apply(item.Card, rating, now);
        store.SaveReview(updated, new ReviewLog(item.Question.Id, now, item.Stage, rating, response, elapsed)
        {
            Grade = grade,
            IntervalBeforeDays = item.IsNew ? 0 : item.Card.IntervalDays,
            DueBefore = item.IsNew ? null : item.Card.DueAt,
            IntervalAfterDays = updated.IntervalDays,
            Client = Client,
        });
        return updated;
    }

    public void Flag(string questionId, string? note) => store.FlagQuestion(questionId, note, Now);

    /// <param name="currentHashes">Each question's current content hash, so leeches ignore answers to an older wording.</param>
    public StatsReport GetStats(IReadOnlyDictionary<string, string>? currentHashes = null)
    {
        var now = Now;
        return StatsCalculator.Compute(
            store.GetActiveQuestions(),
            store.GetCards(),
            store.GetReviewsSince(DateTimeOffset.UnixEpoch),
            store.GetEventsSince(now.AddDays(-StatsCalculator.RecentDays)),
            store.GetStudySettings(),
            now,
            clock.LocalTimeZone,
            currentHashes);
    }
}
