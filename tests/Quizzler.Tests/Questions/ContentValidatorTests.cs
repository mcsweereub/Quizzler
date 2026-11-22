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

    private static Question Exercise() => Valid("cs-exercises-0001") with
    {
        Type = QuestionType.WriteCode, Language = ExerciseLanguage.CSharp, Distractors = [],
        Answer = "int Add(int a, int b) => a + b;", Tests = [new CodeTest("Add(1, 2)", "3")],
    };

    [Fact]
    public void Valid_exercise_needs_no_distractors()
    {
        Assert.Empty(Validate(Exercise()));
    }

    [Fact]
    public void Exercise_rules_are_enforced()
    {
        Assert.Contains(Validate(Exercise() with { Language = null }), i => i.Message.Contains("'language'"));
        Assert.Contains(Validate(Exercise() with { Tests = [] }), i => i.Message.Contains("at least one"));
        Assert.Contains(Validate(Exercise() with { Distractors = ["x", "y"] }), i => i.Message.Contains("no distractors"));
        Assert.Contains(Validate(Exercise() with { Language = ExerciseLanguage.Sql }), i => i.Message.Contains("require 'setup'"));
        Assert.Contains(Validate(Valid() with { Starter = "x" }), i => i.Message.Contains("only for write-code"));
    }

    [Fact]
    public async Task Verifier_rejects_a_reference_solution_that_fails_its_tests()
    {
        var broken = Exercise() with { Answer = "int Add(int a, int b) => a - b;" };
        var trivial = Exercise() with { Starter = "int Add(int a, int b) => a + b;" };

        Assert.Contains(await Quizzler.CodeRunner.ContentVerifier.VerifyExerciseAsync(broken), m => m.Contains("doesn't pass"));
        Assert.Contains(await Quizzler.CodeRunner.ContentVerifier.VerifyExerciseAsync(trivial), m => m.Contains("starter code already passes"));
        Assert.Empty(await Quizzler.CodeRunner.ContentVerifier.VerifyExerciseAsync(Exercise()));
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
