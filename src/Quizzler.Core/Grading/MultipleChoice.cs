using Quizzler.Core.Questions;

namespace Quizzler.Core.Grading;

public sealed record MultipleChoiceOptions(IReadOnlyList<string> Options, int CorrectIndex)
{
    public bool IsCorrect(int chosenIndex) => chosenIndex == CorrectIndex;
}

public static class MultipleChoice
{
    /// <summary>Number of wrong options shown alongside the right one (fewer if the question has fewer).</summary>
    public const int DistractorsShown = 3;

    /// <summary>Picks a random subset of distractors and shuffles them with the correct answer.</summary>
    public static MultipleChoiceOptions Build(Question question, Random random)
    {
        var distractors = question.Distractors.ToArray();
        random.Shuffle(distractors);

        var options = distractors.Take(DistractorsShown).Append(question.Answer).ToArray();
        random.Shuffle(options);

        return new MultipleChoiceOptions(options, Array.IndexOf(options, question.Answer));
    }
}
