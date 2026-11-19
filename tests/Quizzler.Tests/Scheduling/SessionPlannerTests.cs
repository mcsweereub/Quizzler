using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;

namespace Quizzler.Tests.Scheduling;

public class SessionPlannerTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(11);
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, Offset);

    private static Question Q(string id, int difficulty = 1) => new() { Id = id, Difficulty = difficulty };

    private static CardState Card(string id, DateTimeOffset due, DateTimeOffset? firstReviewed = null) => new()
    {
        QuestionId = id,
        DueAt = due,
        ReviewCount = 1,
        FirstReviewedAt = firstReviewed ?? Now.AddDays(-30),
    };

    [Fact]
    public void Includes_due_reviews_oldest_first_and_excludes_future_ones()
    {
        var questions = new[] { Q("a-0001"), Q("a-0002"), Q("a-0003") };
        var cards = new[]
        {
            Card("a-0001", Now.AddHours(-1)),
            Card("a-0002", Now.AddDays(-2)),
            Card("a-0003", Now.AddHours(1)),
        };

        var plan = SessionPlanner.Plan(questions, cards, new StudySettings(), Now);

        Assert.Equal(["a-0002", "a-0001"], plan.Items.Select(i => i.Question.Id));
        Assert.Equal(2, plan.DueReviews);
        Assert.Equal(0, plan.NewCards);
    }

    [Fact]
    public void New_questions_are_limited_per_day_easiest_first()
    {
        var questions = Enumerable.Range(1, 20).Select(i => Q($"a-{i:0000}", difficulty: i % 3 + 1)).ToArray();

        var plan = SessionPlanner.Plan(questions, [], new StudySettings { NewPerDay = 5 }, Now);

        Assert.Equal(5, plan.NewCards);
        Assert.All(plan.Items, i => Assert.True(i.IsNew));
        Assert.All(plan.Items, i => Assert.Equal(1, i.Question.Difficulty));
        Assert.All(plan.Items, i => Assert.Equal(CardStage.MultipleChoice, i.Stage));
    }

    [Fact]
    public void Questions_introduced_today_count_against_the_new_limit()
    {
        var questions = Enumerable.Range(1, 20).Select(i => Q($"a-{i:0000}")).ToArray();
        var introducedEarlierToday = Enumerable.Range(1, 3)
            .Select(i => Card($"a-{i:0000}", Now.AddDays(1), firstReviewed: Now.AddHours(-2)))
            .ToArray();

        var plan = SessionPlanner.Plan(questions, introducedEarlierToday, new StudySettings { NewPerDay = 5 }, Now);

        Assert.Equal(2, plan.NewCards);
        Assert.DoesNotContain(plan.Items, i => i.Question.Id is "a-0001" or "a-0002" or "a-0003");
    }

    [Fact]
    public void Study_day_starts_at_rollover_hour()
    {
        var at2am = new DateTimeOffset(2026, 11, 18, 2, 0, 0, Offset);
        var at5am = new DateTimeOffset(2026, 11, 18, 5, 0, 0, Offset);

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 4, 0, 0, Offset), SessionPlanner.StartOfStudyDay(at2am, 4));
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 4, 0, 0, Offset), SessionPlanner.StartOfStudyDay(at5am, 4));
    }

    [Fact]
    public void Cards_for_deleted_questions_are_ignored()
    {
        var plan = SessionPlanner.Plan([Q("a-0001")], [Card("gone-0001", Now.AddDays(-1))], new StudySettings { NewPerDay = 0 }, Now);
        Assert.True(plan.IsEmpty);
    }
}
