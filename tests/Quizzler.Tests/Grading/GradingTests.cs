using Quizzler.Core.Grading;
using Quizzler.Core.Questions;

namespace Quizzler.Tests.Grading;

public class AnswerCheckerTests
{
    private static Question Concept(string answer, params string[] accepted) =>
        new() { Type = QuestionType.Concept, Answer = answer, Accepted = accepted };

    private static Question Output(string answer) =>
        new() { Type = QuestionType.PredictOutput, Answer = answer };

    [Theory]
    [InlineData("sealed")]
    [InlineData("  Sealed ")]
    [InlineData("`sealed`")]
    [InlineData("sealed.")]
    public void Concept_answers_ignore_case_whitespace_and_decoration(string typed)
    {
        Assert.Equal(CheckResult.Correct, AnswerChecker.Check(Concept("sealed"), typed));
    }

    [Fact]
    public void Concept_answers_ignore_trailing_parentheses()
    {
        Assert.Equal(CheckResult.Correct, AnswerChecker.Check(Concept("GetHashCode"), "gethashcode()"));
    }

    [Fact]
    public void Accepted_alternatives_are_correct()
    {
        Assert.Equal(CheckResult.Correct, AnswerChecker.Check(Concept("Garbage collector", "GC"), "gc"));
    }

    [Fact]
    public void Wrong_concept_answer_is_incorrect()
    {
        Assert.Equal(CheckResult.Incorrect, AnswerChecker.Check(Concept("sealed"), "static"));
    }

    [Fact]
    public void Output_is_case_sensitive()
    {
        Assert.Equal(CheckResult.Incorrect, AnswerChecker.Check(Output("True"), "true"));
    }

    [Fact]
    public void Output_ignores_line_ending_style_and_trailing_whitespace()
    {
        Assert.Equal(CheckResult.Correct, AnswerChecker.Check(Output("1\n2\n3"), "1  \r\n2\r\n3\r\n\r\n"));
    }

    [Fact]
    public void Output_preserves_internal_spacing()
    {
        Assert.Equal(CheckResult.Incorrect, AnswerChecker.Check(Output("a b"), "a  b"));
    }

    [Fact]
    public void Pick_the_code_is_self_graded_by_default()
    {
        var q = new Question { Type = QuestionType.PickTheCode, Answer = "x" };
        Assert.Equal(CheckResult.NeedsSelfGrade, AnswerChecker.Check(q, "x"));
    }

    [Fact]
    public void Recall_override_forces_self_grading()
    {
        var q = Concept("A long explanation") with { Recall = RecallGrading.Self };
        Assert.Equal(CheckResult.NeedsSelfGrade, AnswerChecker.Check(q, "anything"));
    }
}

public class MultipleChoiceTests
{
    [Fact]
    public void Includes_answer_once_with_correct_index_and_at_most_three_distractors()
    {
        var q = new Question { Answer = "right", Distractors = ["w1", "w2", "w3", "w4", "w5"] };

        for (var seed = 0; seed < 50; seed++)
        {
            var mc = MultipleChoice.Build(q, new Random(seed));
            Assert.Equal(4, mc.Options.Count);
            Assert.Single(mc.Options, o => o == "right");
            Assert.Equal("right", mc.Options[mc.CorrectIndex]);
            Assert.True(mc.IsCorrect(mc.CorrectIndex));
        }
    }

    [Fact]
    public void Answer_position_varies()
    {
        var q = new Question { Answer = "right", Distractors = ["w1", "w2", "w3"] };
        var positions = Enumerable.Range(0, 50).Select(s => MultipleChoice.Build(q, new Random(s)).CorrectIndex).Distinct();
        Assert.True(positions.Count() > 1);
    }
}
