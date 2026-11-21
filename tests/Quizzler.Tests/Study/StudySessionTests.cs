using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;

namespace Quizzler.Tests.Study;

public class StudySessionTests
{
    private sealed class InMemoryStore(params Question[] questions) : IQuizStore
    {
        public Dictionary<string, CardState> Cards { get; } = [];
        public List<ReviewLog> Logs { get; } = [];
        public IReadOnlyList<Question> GetActiveQuestions() => questions;
        public IReadOnlyList<CardState> GetCards() => Cards.Values.ToList();
        public void SaveReview(CardState card, ReviewLog log) { Cards[card.QuestionId] = card; Logs.Add(log); }
        public StudySettings GetStudySettings() => new();
        public void FlagQuestion(string questionId, string? note, DateTimeOffset now) { }
        public IReadOnlyList<ReviewLog> GetReviewsSince(DateTimeOffset since) => Logs.Where(l => l.ReviewedAt >= since).ToList();
        public List<(string Kind, string? Detail)> Events { get; } = [];
        public void LogEvent(string kind, string? detail, DateTimeOffset at) => Events.Add((kind, detail));
        public IReadOnlyList<StudyEventLog> GetEventsSince(DateTimeOffset since) => [];
    }

    [Fact]
    public void Records_the_grader_verdict_and_overrides()
    {
        var (session, store) = Start(Concept("a-0001"), Concept("a-0002"));

        session.MoveNext();
        session.SubmitChoice(session.Choices!.CorrectIndex);
        session.Complete(AnswerOutcome.Right);

        // Second question: answer it right, then make it typed and get it "wrong" but override.
        session.MoveNext();
        session.SubmitChoice(session.Choices!.CorrectIndex);
        session.Complete(AnswerOutcome.Guessed);

        Assert.Equal(ReviewGrade.Correct, store.Logs[0].Grade);
        Assert.False(store.Logs[0].WasOverridden);
        Assert.Equal(0, store.Logs[0].IntervalBeforeDays);
        Assert.Null(store.Logs[0].DueBefore);
        Assert.True(store.Logs[0].IntervalAfterDays > 0);
    }

    [Fact]
    public void Typed_override_is_recorded_as_overridden()
    {
        var store = new InMemoryStore(Concept("a-0001"));
        store.Cards["a-0001"] = new CardState { QuestionId = "a-0001", Stage = CardStage.Typed, IntervalDays = 6, DueAt = DateTimeOffset.Now.AddHours(-1), Repetitions = 2 };
        var service = new QuizService(store) { Client = "test" };
        var session = new StudySession(service, service.PlanSession(), new Random(1));

        session.MoveNext();
        session.SubmitTyped("selaed");
        session.Complete(AnswerOutcome.Right);

        var log = Assert.Single(store.Logs);
        Assert.Equal(ReviewGrade.Incorrect, log.Grade);
        Assert.True(log.WasOverridden);
        Assert.Equal(6, log.IntervalBeforeDays);
        Assert.NotNull(log.DueBefore);
        Assert.Equal("test", log.Client);
    }

    [Fact]
    public void Logs_session_start_and_end_once()
    {
        var (session, store) = Start(Concept("a-0001"));
        session.MoveNext();
        session.SubmitChoice(session.Choices!.CorrectIndex);
        session.Complete(AnswerOutcome.Right);
        Assert.False(session.MoveNext());
        session.End();

        Assert.Equal(
            [(StudyEvent.SessionStarted, "0 reviews, 1 new"), (StudyEvent.SessionEnded, "1/1 answered, 1 correct")],
            store.Events);
    }

    private static Question Concept(string id) => new()
    {
        Id = id, Type = QuestionType.Concept, Topic = "t", Difficulty = 1,
        Prompt = "?", Answer = "sealed", Distractors = ["static", "abstract", "readonly"], Explanation = "e",
    };

    private static (StudySession Session, InMemoryStore Store) Start(params Question[] questions)
    {
        var store = new InMemoryStore(questions);
        var service = new QuizService(store);
        return (new StudySession(service, service.PlanSession(), new Random(42)), store);
    }

    [Fact]
    public void Walks_every_item_then_finishes()
    {
        var (session, _) = Start(Concept("a-0001"), Concept("a-0002"));

        Assert.True(session.MoveNext());
        Assert.Equal(1, session.Position);
        session.SubmitChoice(session.Choices!.CorrectIndex);
        session.Complete(AnswerOutcome.Right);

        Assert.True(session.MoveNext());
        Assert.Equal(2, session.Position);
        session.SubmitChoice((session.Choices!.CorrectIndex + 1) % session.Choices.Options.Count);
        session.Complete(AnswerOutcome.Wrong);

        Assert.False(session.MoveNext());
        Assert.True(session.IsFinished);
        Assert.Equal(2, session.Answered);
        Assert.Equal(1, session.CorrectCount);
    }

    [Fact]
    public void New_questions_get_multiple_choice_options()
    {
        var (session, _) = Start(Concept("a-0001"));
        session.MoveNext();

        Assert.NotNull(session.Choices);
        Assert.Equal("sealed", session.Choices.Options[session.Choices.CorrectIndex]);
    }

    [Fact]
    public void Grades_multiple_choice()
    {
        var (session, _) = Start(Concept("a-0001"));
        session.MoveNext();

        var answer = session.SubmitChoice(session.Choices!.CorrectIndex);

        Assert.Equal(AnswerState.Correct, answer.State);
        Assert.Equal("sealed", answer.Response);
        Assert.False(answer.CanOverride);
    }

    [Fact]
    public void Wrong_multiple_choice_cannot_be_marked_right()
    {
        var (session, _) = Start(Concept("a-0001"));
        session.MoveNext();
        session.SubmitChoice((session.Choices!.CorrectIndex + 1) % 4);

        Assert.Throws<InvalidOperationException>(() => session.Complete(AnswerOutcome.Right));
    }

    [Fact]
    public void Typed_wrong_answer_can_be_overridden()
    {
        var store = new InMemoryStore(Concept("a-0001"));
        store.Cards["a-0001"] = new CardState { QuestionId = "a-0001", Stage = CardStage.Typed, ReviewCount = 1, DueAt = DateTimeOffset.Now.AddDays(-1) };
        var service = new QuizService(store);
        var session = new StudySession(service, service.PlanSession());
        session.MoveNext();

        Assert.Null(session.Choices);
        var answer = session.SubmitTyped("it's sealed, obviously");
        Assert.Equal(AnswerState.Incorrect, answer.State);
        Assert.True(answer.CanOverride);

        var card = session.Complete(AnswerOutcome.Right);
        Assert.Equal(CardStage.Typed, card.Stage);
        Assert.Equal("it's sealed, obviously", store.Logs.Single().Response);
    }

    [Fact]
    public void Pick_the_code_typed_answers_need_self_grading()
    {
        var q = Concept("a-0001") with { Type = QuestionType.PickTheCode };
        var store = new InMemoryStore(q);
        store.Cards["a-0001"] = new CardState { QuestionId = "a-0001", Stage = CardStage.Typed, ReviewCount = 1, DueAt = DateTimeOffset.Now.AddDays(-1) };
        var service = new QuizService(store);
        var session = new StudySession(service, service.PlanSession());
        session.MoveNext();

        Assert.Equal(AnswerState.NeedsSelfGrade, session.SubmitTyped("var x = 1;").State);
    }

    [Fact]
    public void Enforces_call_order()
    {
        var (session, _) = Start(Concept("a-0001"));
        Assert.Throws<InvalidOperationException>(() => session.SubmitChoice(0));

        session.MoveNext();
        Assert.Throws<InvalidOperationException>(() => session.Complete(AnswerOutcome.Right));

        session.SubmitChoice(0);
        Assert.Throws<InvalidOperationException>(() => session.SubmitChoice(0));
        Assert.Throws<InvalidOperationException>(() => session.MoveNext());
    }

    [Fact]
    public void Empty_plan_finishes_immediately()
    {
        var (session, _) = Start();
        Assert.False(session.MoveNext());
        Assert.True(session.IsFinished);
    }
}
