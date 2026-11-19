namespace Quizzler.Core.Scheduling;

/// <summary>
/// Applies a review to a card: interval scheduling via <see cref="IScheduler"/>, plus the stage rule:
/// <list type="bullet">
/// <item>New questions are asked as multiple choice.</item>
/// <item>A correct multiple-choice answer promotes the card to typed recall for next time.</item>
/// <item>A wrong answer (in either stage) drops it back to multiple choice.</item>
/// </list>
/// </summary>
public sealed class ReviewEngine(IScheduler scheduler)
{
    public static CardState NewCard(string questionId) => new() { QuestionId = questionId };

    public CardState Apply(CardState card, Rating rating, DateTimeOffset now)
    {
        var scheduled = scheduler.Schedule(card, rating, now);
        var stage = rating == Rating.Again ? CardStage.MultipleChoice : CardStage.Typed;
        return scheduled with { Stage = stage };
    }
}
