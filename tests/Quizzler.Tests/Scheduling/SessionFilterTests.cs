using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;

namespace Quizzler.Tests.Scheduling;

public class SessionFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(11));

    private static Question Concept(string id) => new() { Id = id, Type = QuestionType.Concept, Difficulty = 1 };
    private static Question Exercise(string id, ExerciseLanguage language = ExerciseLanguage.CSharp) =>
        new() { Id = id, Type = QuestionType.WriteCode, Language = language, Difficulty = 1 };
    private static Question Plan(string id) => new() { Id = id, Type = QuestionType.PredictResult, Expect = ResultExpectation.TableAccess, Difficulty = 1 };

    private static CardState Due(string id, CardStage stage) => new()
    {
        QuestionId = id, Stage = stage, DueAt = Now.AddHours(-1), ReviewCount = 1, FirstReviewedAt = Now.AddDays(-10),
    };

    private static readonly Question[] Questions =
    [
        Concept("c-mc-0001"), Concept("c-typed-0001"), Concept("c-new-0001"),
        Exercise("x-due-0001"), Exercise("x-new-0001"), Exercise("x-sql-0001", ExerciseLanguage.Sql), Plan("p-new-0001"),
    ];

    private static readonly CardState[] Cards =
    [
        Due("c-mc-0001", CardStage.MultipleChoice), Due("c-typed-0001", CardStage.Typed), Due("x-due-0001", CardStage.Typed),
    ];

    private static IEnumerable<string> Ids(SessionFilter filter) =>
        SessionPlanner.Plan(Questions, Cards, new StudySettings { NewPerDay = 50 }, Now, filter).Items.Select(i => i.Question.Id).Order();

    [Fact]
    public void Everything_includes_all_due_and_new()
    {
        Assert.Equal(Questions.Select(q => q.Id).Order(), Ids(SessionFilter.Everything));
    }

    [Fact]
    public void Multiple_choice_only_skips_typed_stage_questions_and_exercises_but_keeps_new_ones()
    {
        var ids = Ids(new SessionFilter { Style = AnswerStyle.MultipleChoiceOnly });

        Assert.Equal(["c-mc-0001", "c-new-0001", "p-new-0001"], ids);
    }

    [Fact]
    public void Typed_only_has_no_new_questions_and_no_exercises()
    {
        Assert.Equal(["c-typed-0001"], Ids(new SessionFilter { Style = AnswerStyle.TypedOnly }));
    }

    [Fact]
    public void Kinds_select_coding_exercises_by_language()
    {
        Assert.Equal(["x-due-0001", "x-new-0001", "x-sql-0001"], Ids(new SessionFilter { Kinds = QuestionKinds.Exercises.ToHashSet() }));
        Assert.Equal(["x-sql-0001"], Ids(new SessionFilter { Kinds = new HashSet<QuestionKind> { QuestionKind.SqlExercise } }));
        Assert.Equal(["p-new-0001"], Ids(new SessionFilter { Kinds = new HashSet<QuestionKind> { QuestionKind.QueryPlan } }));
    }

    [Fact]
    public void New_per_day_allowance_is_shared_across_filtered_sessions()
    {
        var introducedToday = Cards.Append(new CardState { QuestionId = "c-new-0001", DueAt = Now.AddDays(1), FirstReviewedAt = Now.AddHours(-1) }).ToArray();
        var plan = SessionPlanner.Plan(Questions, introducedToday, new StudySettings { NewPerDay = 2 }, Now,
            new SessionFilter { Kinds = QuestionKinds.Exercises.ToHashSet() });

        Assert.Equal(1, plan.NewCards); // one of today's two was already used by a concept question
    }

    [Fact]
    public void Serialize_and_parse_round_trip()
    {
        var filter = new SessionFilter { Style = AnswerStyle.MultipleChoiceOnly, Kinds = new HashSet<QuestionKind> { QuestionKind.SqlResult, QuestionKind.Concept } };

        var text = filter.Serialize();
        var parsed = SessionFilter.Parse(text);

        Assert.Equal("style=mc;kinds=concept,sql-result", text);
        Assert.Equal(filter.Style, parsed.Style);
        Assert.Equal(filter.Kinds, parsed.Kinds);
        Assert.True(SessionFilter.Parse(null).IsEverything);
        Assert.True(SessionFilter.Parse("style=mixed;kinds=all").IsEverything);
        Assert.Equal("Multiple choice only · Concepts + SQL: predict the result", filter.Describe());
    }

    [Fact]
    public void Exercise_submission_records_pass_or_fail_and_is_never_multiple_choice()
    {
        var store = new ExerciseStore(Exercise("x-new-0001"));
        var service = new QuizService(store);
        var session = new StudySession(service, service.PlanSession(), new Random(1));

        Assert.True(session.MoveNext());
        Assert.Null(session.Choices);
        Assert.Throws<InvalidOperationException>(() => session.SubmitTyped("x"));
        var pending = session.SubmitExercise("int Add(int a, int b) => a + b;", passed: true);
        session.Complete(AnswerOutcome.Guessed);

        Assert.Equal(AnswerState.Correct, pending.State);
        var log = Assert.Single(store.Logs);
        Assert.Equal(ReviewGrade.Correct, log.Grade);
        Assert.Equal(Rating.Hard, log.Rating);
        Assert.Equal("int Add(int a, int b) => a + b;", log.Response);
    }

    private sealed class ExerciseStore(params Question[] questions) : IQuizStore
    {
        public List<ReviewLog> Logs { get; } = [];
        private readonly Dictionary<string, CardState> _cards = [];
        public IReadOnlyList<Question> GetActiveQuestions() => questions;
        public IReadOnlyList<CardState> GetCards() => _cards.Values.ToList();
        public void SaveReview(CardState card, ReviewLog log) { _cards[card.QuestionId] = card; Logs.Add(log); }
        public IReadOnlyList<ReviewLog> GetReviewsSince(DateTimeOffset since) => Logs;
        public StudySettings GetStudySettings() => new();
        public void FlagQuestion(string questionId, string? note, DateTimeOffset now) { }
        public void LogEvent(string kind, string? detail, DateTimeOffset at) { }
        public IReadOnlyList<StudyEventLog> GetEventsSince(DateTimeOffset since) => [];
    }
}
