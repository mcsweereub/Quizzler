using Quizzler.Core.Scheduling;

namespace Quizzler.Tests.Scheduling;

public class ReviewEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
    private readonly ReviewEngine _engine = new(new Sm2Scheduler());

    [Fact]
    public void New_cards_start_as_multiple_choice()
    {
        Assert.Equal(CardStage.MultipleChoice, ReviewEngine.NewCard("q-0001").Stage);
    }

    [Theory]
    [InlineData(Rating.Hard)]
    [InlineData(Rating.Good)]
    [InlineData(Rating.Easy)]
    public void Correct_multiple_choice_promotes_to_typed(Rating rating)
    {
        var card = _engine.Apply(ReviewEngine.NewCard("q-0001"), rating, Now);
        Assert.Equal(CardStage.Typed, card.Stage);
    }

    [Fact]
    public void Wrong_multiple_choice_stays_multiple_choice()
    {
        var card = _engine.Apply(ReviewEngine.NewCard("q-0001"), Rating.Again, Now);
        Assert.Equal(CardStage.MultipleChoice, card.Stage);
    }

    [Fact]
    public void Wrong_typed_answer_drops_back_to_multiple_choice()
    {
        var typed = _engine.Apply(ReviewEngine.NewCard("q-0001"), Rating.Good, Now);
        var failed = _engine.Apply(typed, Rating.Again, Now.AddDays(1));
        Assert.Equal(CardStage.MultipleChoice, failed.Stage);
    }

    [Fact]
    public void Correct_typed_answer_stays_typed()
    {
        var typed = _engine.Apply(ReviewEngine.NewCard("q-0001"), Rating.Good, Now);
        var again = _engine.Apply(typed, Rating.Good, Now.AddDays(1));
        Assert.Equal(CardStage.Typed, again.Stage);
    }
}
