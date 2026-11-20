using Quizzler.CodeRunner;
using Quizzler.Core.Grading;
using Quizzler.Core.Questions;
using Quizzler.Data;

namespace Quizzler.Tests.Data;

public class SqlSandboxTests
{
    private const string Setup = """
        CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT);
        INSERT INTO Customers VALUES (1, 'Ada'), (2, 'Bob'), (3, 'Cy');
        CREATE TABLE Orders (Id INTEGER PRIMARY KEY, CustomerId INTEGER, Total INTEGER);
        INSERT INTO Orders VALUES (10, 1, 50), (11, 1, 20), (12, 2, 30);
        """;

    private static Question Sql(string query, string answer, string[]? distractors = null, ResultExpectation? expect = null) => new()
    {
        Id = "sql-test-0001",
        Type = QuestionType.PredictResult,
        Topic = "sql/test",
        Difficulty = 2,
        Prompt = "What does this query return?",
        Setup = Setup,
        Code = query,
        Expect = expect,
        Answer = answer,
        Distractors = distractors ?? ["x", "y"],
        Explanation = "Because.",
    };

    [Fact]
    public void Reads_setup_tables_in_creation_order_with_columns_and_rows()
    {
        var run = SqlSandbox.Run(Setup, "SELECT 1");

        Assert.Equal(["Customers", "Orders"], run.Tables.Select(t => t.Name));
        Assert.Equal(["Id", "Name"], run.Tables[0].Columns);
        Assert.Equal(["1", "Ada"], run.Tables[0].Rows[0]);
        Assert.Equal(3, run.Tables[1].Rows.Count);
    }

    [Fact]
    public void Formats_nulls_reals_and_empty_results()
    {
        var run = SqlSandbox.Run(Setup, "SELECT NULL, 2.5, AVG(Total) FROM Orders");
        Assert.Equal("NULL, 2.5, 33.333333333333336", run.Result.RowsText);

        Assert.Equal(SqlSandbox.NoRows, SqlSandbox.Run(Setup, "SELECT * FROM Orders WHERE 0").Result.RowsText);
    }

    [Fact]
    public void Row_analysis_marks_rows_whose_deletion_changes_the_result()
    {
        // Only Ada's orders (10, 11) and Ada herself affect "Ada's total"; Bob, Cy and order 12 don't.
        var run = SqlSandbox.Run(Setup,
            "SELECT SUM(o.Total) FROM Customers c JOIN Orders o ON o.CustomerId = c.Id WHERE c.Name = 'Ada'",
            analyseRows: true);

        Assert.Equal([true, false, false], run.RowMatters![0]);
        Assert.Equal([true, true, false], run.RowMatters[1]);
    }

    [Fact]
    public void Row_analysis_leaves_the_data_unchanged()
    {
        var run = SqlSandbox.Run(Setup, "SELECT COUNT(*) FROM Orders", analyseRows: true);
        Assert.Equal("3", run.Result.RowsText);
        Assert.All(run.RowMatters![1], Assert.True);
    }

    [Fact]
    public void Bad_sql_throws_a_readable_error()
    {
        var ex = Assert.Throws<SqlRunException>(() => SqlSandbox.Run(Setup, "SELECT Nope FROM Orders"));
        Assert.Contains("no such column", ex.Message);
    }

    [Theory]
    [InlineData("Ada, 50\nBob, 30", "ada,50\n\n bob , 30 ")]
    [InlineData("Ada, 50", "'Ada' | 50")]
    [InlineData("Cy, NULL", "cy, null")]
    public void Typed_results_ignore_case_spacing_quotes_and_separators(string expected, string typed)
    {
        Assert.True(AnswerChecker.Matches(QuestionType.PredictResult, expected, typed));
    }

    [Fact]
    public void Typed_results_still_care_about_row_order_and_values()
    {
        Assert.False(AnswerChecker.Matches(QuestionType.PredictResult, "Ada, 50\nBob, 30", "Bob, 30\nAda, 50"));
        Assert.False(AnswerChecker.Matches(QuestionType.PredictResult, "Ada, 50", "Ada, 5"));
    }

    [Fact]
    public void Verifier_accepts_a_correct_answer_key()
    {
        var q = Sql("SELECT Name FROM Customers ORDER BY Id", "Ada\nBob\nCy");
        Assert.Empty(ContentVerifier.VerifySql(q));
    }

    [Fact]
    public void Verifier_reports_a_wrong_answer_key_with_the_real_result()
    {
        var q = Sql("SELECT Name FROM Customers ORDER BY Id", "Ada\nBob");
        var message = Assert.Single(ContentVerifier.VerifySql(q));
        Assert.Contains("Answer key is wrong", message);
        Assert.Contains("Cy", message);
    }

    [Fact]
    public void Verifier_reports_a_distractor_that_is_actually_correct()
    {
        var q = Sql("SELECT COUNT(*) FROM Orders", "3", ["3 ", "4"]);
        Assert.Contains(ContentVerifier.VerifySql(q), m => m.Contains("Distractor matches"));
    }

    [Fact]
    public void Verifier_requires_order_by_for_multi_row_answers()
    {
        var q = Sql("SELECT Name FROM Customers", "Ada\nBob\nCy");
        Assert.Contains(ContentVerifier.VerifySql(q), m => m.Contains("ORDER BY"));
    }

    [Fact]
    public void Verifier_checks_row_counts_without_needing_order_by()
    {
        var q = Sql("SELECT * FROM Orders", "3", ["2", "4"], ResultExpectation.RowCount);
        Assert.Empty(ContentVerifier.VerifySql(q));
    }

    [Fact]
    public void Verifier_reports_sql_errors()
    {
        var q = Sql("SELEC 1", "1");
        Assert.Contains(ContentVerifier.VerifySql(q), m => m.StartsWith("SQL error"));
    }

    [Fact]
    public void Validator_requires_setup_and_query_for_predict_result()
    {
        var q = Sql("SELECT 1", "1") with { Setup = null, Code = null };
        var issues = ContentValidator.Validate([new LoadedQuestion(q, "test.json")]);
        Assert.Contains(issues, i => i.Message.Contains("'setup'"));
        Assert.Contains(issues, i => i.Message.Contains("'code'"));
    }
}
