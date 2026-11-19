using Quizzler.CodeRunner;
using Quizzler.Core.Grading;
using Quizzler.Core.Questions;

namespace Quizzler.Tests.Questions;

/// <summary>
/// Validates the real question bank in content/. Run `dotnet test` after adding or generating questions.
/// </summary>
public class ContentBankTests
{
    private static readonly Lazy<ContentLoadResult> Bank = new(() =>
        ContentLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "content")));

    public static TheoryData<string> PredictOutputIds()
    {
        var data = new TheoryData<string>();
        foreach (var q in Bank.Value.Questions.Where(q => q.Question.Type == QuestionType.PredictOutput))
            data.Add(q.Question.Id);
        return data;
    }

    [Fact]
    public void Content_bank_is_not_empty()
    {
        Assert.NotEmpty(Bank.Value.Questions);
    }

    [Fact]
    public void Content_bank_passes_validation()
    {
        Assert.True(Bank.Value.IsValid, "Content issues:\n" + string.Join("\n", Bank.Value.Issues));
    }

    /// <summary>Compiles and runs every predict-output snippet and checks the answer key against real output.</summary>
    [Theory]
    [MemberData(nameof(PredictOutputIds))]
    public async Task Predict_output_answer_matches_actual_output(string id)
    {
        var q = Bank.Value.Questions.Single(l => l.Question.Id == id).Question;

        var result = await CSharpRunner.RunAsync(q.Code!);

        Assert.True(result.Compiled, $"{id} does not compile:\n" + string.Join("\n", result.Errors));
        Assert.False(result.TimedOut, $"{id} timed out.");
        Assert.True(
            AnswerChecker.Matches(QuestionType.PredictOutput, q.Answer, result.Output),
            $"{id}: answer key says\n{q.Answer}\nbut the code printed\n{AnswerChecker.NormalizeOutput(result.Output)}");

        foreach (var distractor in q.Distractors)
            Assert.False(AnswerChecker.Matches(QuestionType.PredictOutput, distractor, result.Output), $"{id}: a distractor matches the real output.");
    }
}
