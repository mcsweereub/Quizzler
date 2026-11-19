using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;
using Quizzler.Data;

namespace Quizzler.Tests.Data;

public sealed class QuizDatabaseTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 9, 0, 0, TimeSpan.FromHours(11));
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"quizzler-test-{Guid.NewGuid():N}.db");
    private readonly QuizDatabase _db;

    public QuizDatabaseTests() => _db = QuizDatabase.Open(_path);

    public void Dispose() => File.Delete(_path);

    internal static Question Q(string id, string prompt = "Prompt?") => new()
    {
        Id = id,
        Type = QuestionType.Concept,
        Topic = "csharp/test",
        Difficulty = 2,
        Tags = ["test"],
        Prompt = prompt,
        Answer = "answer",
        Accepted = ["alt"],
        Distractors = ["d1", "d2", "d3"],
        Explanation = "because",
    };

    private static LoadedQuestion[] Loaded(params Question[] qs) => qs.Select(q => new LoadedQuestion(q, "test.json")).ToArray();

    [Fact]
    public void Reopening_an_existing_database_is_safe()
    {
        _db.ImportQuestions(Loaded(Q("t-0001")), Now);
        var reopened = QuizDatabase.Open(_path);
        Assert.Single(reopened.GetActiveQuestions());
    }

    [Fact]
    public void Import_round_trips_every_question_field()
    {
        var q = Q("t-0001") with { Type = QuestionType.PredictOutput, Code = "Console.Write(1);", Recall = RecallGrading.Self };
        _db.ImportQuestions(Loaded(q), Now);

        var stored = Assert.Single(_db.GetActiveQuestions());
        Assert.Equal(q.Id, stored.Id);
        Assert.Equal(q.Type, stored.Type);
        Assert.Equal(q.Code, stored.Code);
        Assert.Equal(q.Recall, stored.Recall);
        Assert.Equal(q.Distractors, stored.Distractors);
        Assert.Equal(q.Accepted, stored.Accepted);
        Assert.Equal(q.Tags, stored.Tags);
    }

    [Fact]
    public void Import_reports_added_updated_unchanged_retired_and_restored()
    {
        var first = _db.ImportQuestions(Loaded(Q("t-0001"), Q("t-0002")), Now);
        Assert.Equal(["t-0001", "t-0002"], first.Added);

        var second = _db.ImportQuestions(Loaded(Q("t-0001", "Changed?")), Now);
        Assert.Equal(["t-0001"], second.Updated);
        Assert.Equal(["t-0002"], second.Retired);
        Assert.Single(_db.GetActiveQuestions());

        var third = _db.ImportQuestions(Loaded(Q("t-0001", "Changed?"), Q("t-0002")), Now);
        Assert.Equal(1, third.Unchanged);
        Assert.Equal(["t-0002"], third.Restored);
        Assert.Empty(third.Updated);
        Assert.Equal(2, _db.GetActiveQuestions().Count);
    }

    [Fact]
    public void Progress_survives_retire_and_restore()
    {
        _db.ImportQuestions(Loaded(Q("t-0001")), Now);
        var card = ReviewEngine.NewCard("t-0001") with { Stage = CardStage.Typed, ReviewCount = 3, DueAt = Now.AddDays(5) };
        _db.SaveReview(card, new ReviewLog("t-0001", Now, CardStage.MultipleChoice, Rating.Good, "answer", TimeSpan.FromSeconds(4)));

        _db.ImportQuestions([], Now);
        _db.ImportQuestions(Loaded(Q("t-0001")), Now);

        var restored = Assert.Single(_db.GetCards());
        Assert.Equal(3, restored.ReviewCount);
        Assert.Equal(CardStage.Typed, restored.Stage);
    }

    [Fact]
    public void SaveReview_upserts_card_and_appends_log()
    {
        _db.ImportQuestions(Loaded(Q("t-0001")), Now);
        var engine = new ReviewEngine(new Sm2Scheduler());

        var card = engine.Apply(ReviewEngine.NewCard("t-0001"), Rating.Good, Now);
        _db.SaveReview(card, new ReviewLog("t-0001", Now, CardStage.MultipleChoice, Rating.Good, "answer", TimeSpan.FromSeconds(3)));
        var later = Now.AddDays(1);
        var card2 = engine.Apply(card, Rating.Again, later);
        _db.SaveReview(card2, new ReviewLog("t-0001", later, CardStage.Typed, Rating.Again, "wrong", TimeSpan.FromSeconds(9)));

        var stored = Assert.Single(_db.GetCards());
        Assert.Equal(card2, stored); // DateTimeOffset equality compares instants, so UTC storage round-trips equal

        var logs = _db.GetReviewsSince(Now.AddHours(-1));
        Assert.Equal(2, logs.Count);
        Assert.Equal(Rating.Again, logs[1].Rating);
        Assert.Equal("wrong", logs[1].Response);
        Assert.Equal(TimeSpan.FromSeconds(9), logs[1].Elapsed);
        Assert.Equal(CardStage.Typed, logs[1].Stage);
    }

    [Fact]
    public void Flags_can_be_added_listed_and_resolved()
    {
        _db.FlagQuestion("t-0001", "  answer is wrong  ", Now);
        _db.FlagQuestion("t-0001", "", Now.AddMinutes(1));

        var open = _db.GetFlags();
        Assert.Equal(2, open.Count);
        Assert.Equal("answer is wrong", open[0].Note);
        Assert.Null(open[1].Note);

        Assert.Equal(2, _db.ResolveFlags("t-0001", Now));
        Assert.Empty(_db.GetFlags());
        Assert.Equal(2, _db.GetFlags(includeResolved: true).Count);
    }

    [Fact]
    public void Study_settings_default_then_persist()
    {
        Assert.Equal(new StudySettings(), _db.GetStudySettings());

        _db.SaveStudySettings(new StudySettings { NewPerDay = 25, DayRolloverHour = 3 });

        Assert.Equal(new StudySettings { NewPerDay = 25, DayRolloverHour = 3 }, QuizDatabase.Open(_path).GetStudySettings());
    }

    [Fact]
    public void Notification_settings_default_then_persist()
    {
        Assert.Equal(new NotificationSettings(), _db.GetNotificationSettings());

        var custom = new NotificationSettings { Enabled = false, ReminderIntervalMinutes = 30, QuietStartHour = 21, QuietEndHour = 7 };
        _db.SaveNotificationSettings(custom);

        Assert.Equal(custom, QuizDatabase.Open(_path).GetNotificationSettings());
    }

    [Fact]
    public void Raw_settings_round_trip()
    {
        Assert.Null(_db.GetSetting("x"));
        _db.SetSetting("x", "1");
        _db.SetSetting("x", "2");
        Assert.Equal("2", _db.GetSetting("x"));
    }

    [Fact]
    public void Invalid_settings_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _db.SaveStudySettings(new StudySettings { DayRolloverHour = 24 }));
    }
}
