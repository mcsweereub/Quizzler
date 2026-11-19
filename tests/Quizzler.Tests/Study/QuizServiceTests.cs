using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;
using Quizzler.Data;
using Quizzler.Tests.Data;

namespace Quizzler.Tests.Study;

/// <summary>End-to-end: the real SQLite store driven through the study loop with a controllable clock.</summary>
public sealed class QuizServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"quizzler-test-{Guid.NewGuid():N}.db");
    private readonly QuizDatabase _db;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(11)));
    private readonly QuizService _service;

    public QuizServiceTests()
    {
        _db = QuizDatabase.Open(_path);
        _db.SaveStudySettings(new StudySettings { NewPerDay = 3 });
        var questions = Enumerable.Range(1, 5).Select(i => new LoadedQuestion(QuizDatabaseTests.Q($"t-{i:0000}"), "test.json"));
        _db.ImportQuestions(questions, _clock.GetUtcNow());
        _service = new QuizService(_db, new ReviewEngine(new Sm2Scheduler()), _clock);
    }

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void First_session_offers_new_questions_up_to_daily_limit_as_multiple_choice()
    {
        var plan = _service.PlanSession();

        Assert.Equal(3, plan.NewCards);
        Assert.All(plan.Items, i => Assert.Equal(CardStage.MultipleChoice, i.Stage));
        Assert.Equal(3, _service.GetDueSummary().Total);
    }

    [Fact]
    public void Correct_answer_promotes_to_typed_and_schedules_tomorrow()
    {
        var item = _service.PlanSession().Items[0];

        _service.RecordAnswer(item, AnswerOutcome.Right, "answer", TimeSpan.FromSeconds(5));

        var card = Assert.Single(_db.GetCards());
        Assert.Equal(CardStage.Typed, card.Stage);
        Assert.Equal(_clock.GetLocalNow().AddDays(1), card.DueAt);
    }

    [Fact]
    public void Answered_questions_use_up_todays_new_allowance()
    {
        foreach (var item in _service.PlanSession().Items)
            _service.RecordAnswer(item, AnswerOutcome.Right, "answer", TimeSpan.FromSeconds(1));

        var summary = _service.GetDueSummary();
        Assert.Equal(0, summary.Total);
        Assert.Equal(_clock.GetLocalNow().AddDays(1), summary.NextDueAt);

        // Next day: yesterday's 3 are due as typed reviews, plus a fresh new allowance (only 2 left in the bank).
        _clock.Advance(TimeSpan.FromDays(1));
        var plan = _service.PlanSession();
        Assert.Equal(3, plan.DueReviews);
        Assert.Equal(2, plan.NewCards);
        Assert.All(plan.Items.Where(i => !i.IsNew), i => Assert.Equal(CardStage.Typed, i.Stage));
    }

    [Fact]
    public void Wrong_answer_comes_back_in_ten_minutes_as_multiple_choice()
    {
        var item = _service.PlanSession().Items[0];
        _service.RecordAnswer(item, AnswerOutcome.Wrong, "nope", TimeSpan.FromSeconds(5));

        _clock.Advance(TimeSpan.FromMinutes(11));
        var due = _service.PlanSession().Items.Where(i => !i.IsNew).ToList();

        var again = Assert.Single(due);
        Assert.Equal(item.Question.Id, again.Question.Id);
        Assert.Equal(CardStage.MultipleChoice, again.Stage);
    }

    [Fact]
    public void Flags_are_stored()
    {
        _service.Flag("t-0001", "typo");
        Assert.Equal("typo", Assert.Single(_db.GetFlags()).Note);
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("Test", start.Offset, "Test", "Test");
        public void Advance(TimeSpan by) => _now += by;
    }
}
