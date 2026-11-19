using Quizzler.CodeRunner;
using Quizzler.Core.Questions;

namespace Quizzler.Tests.Questions;

/// <summary>
/// Validates the real question bank in content/. Run `dotnet test --filter ContentBankTests`
/// (or `quizzler validate`) after adding or generating questions.
/// </summary>
public class ContentBankTests
{
    private static readonly Lazy<ContentLoadResult> Bank = new(() =>
        ContentLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "content")));

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
    [Fact]
    public async Task Predict_output_answers_match_actual_output()
    {
        var issues = await ContentVerifier.VerifyAsync(Bank.Value.Questions);
        Assert.True(issues.Count == 0, "Verification failures:\n" + string.Join("\n", issues));
    }
}
