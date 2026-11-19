using Quizzler.Core.Questions;

namespace Quizzler.Tests.Questions;

public class ContentValidatorTests
{
    private static Question Valid(string id = "cs-test-0001") => new()
    {
        Id = id,
        Type = QuestionType.Concept,
        Topic = "csharp/test",
        Difficulty = 2,
        Tags = ["test"],
        Prompt = "Which keyword prevents a class being inherited from?",
        Answer = "sealed",
        Distractors = ["static", "abstract", "readonly"],
        Explanation = "sealed classes cannot be derived from.",
    };

    private static IReadOnlyList<ContentIssue> Validate(params Question[] questions) =>
        ContentValidator.Validate(questions.Select(q => new LoadedQuestion(q, "test.json")).ToList());

    [Fact]
    public void Valid_question_has_no_issues()
    {
        Assert.Empty(Validate(Valid()));
    }

    [Fact]
    public void Duplicate_ids_are_reported()
    {
        Assert.Contains(Validate(Valid(), Valid()), i => i.Message.Contains("Duplicate id"));
    }

    [Theory]
    [InlineData("CS-test-0001")]
    [InlineData("cs-test-1")]
    [InlineData("cs test 0001")]
    public void Malformed_ids_are_reported(string id)
    {
        Assert.Contains(Validate(Valid(id)), i => i.Message.Contains("Id"));
    }

    [Fact]
    public void Distractor_that_matches_answer_is_reported()
    {
        var q = Valid() with { Distractors = ["Sealed.", "static", "abstract"] };
        Assert.Contains(Validate(q), i => i.Message.Contains("matches a correct answer"));
    }

    [Fact]
    public void Too_few_distractors_is_reported()
    {
        Assert.Contains(Validate(Valid() with { Distractors = ["static"] }), i => i.Message.Contains("distractors"));
    }

    [Fact]
    public void Predict_output_without_code_is_reported()
    {
        var q = Valid() with { Type = QuestionType.PredictOutput };
        Assert.Contains(Validate(q), i => i.Message.Contains("require 'code'"));
    }

    [Fact]
    public void Long_exact_concept_answer_is_reported_unless_self_graded()
    {
        var q = Valid() with { Answer = new string('x', 80) };
        Assert.Contains(Validate(q), i => i.Message.Contains("Shorten"));
        Assert.Empty(Validate(q with { Recall = RecallGrading.Self }));
    }
}
