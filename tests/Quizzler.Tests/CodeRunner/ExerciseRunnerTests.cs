using Quizzler.CodeRunner;
using Quizzler.Core.Questions;

namespace Quizzler.Tests.CodeRunner;

public class ExerciseRunnerTests
{
    private static Question CSharp(params CodeTest[] tests) => new()
    {
        Id = "cs-exercises-9001", Type = QuestionType.WriteCode, Language = ExerciseLanguage.CSharp, Topic = "csharp/exercises",
        Difficulty = 2, Prompt = "Write Add.", Answer = "int Add(int a, int b) => a + b;", Explanation = "x",
        Tests = tests.Length > 0 ? tests : [new("Add(1, 2)", "3"), new("Add(-1, 1)", "0")],
    };

    [Fact]
    public async Task Passing_solution_passes_every_test()
    {
        var result = await ExerciseRunner.RunAsync(CSharp(), "int Add(int a, int b) => a + b;");

        Assert.Equal(ExerciseStatus.Passed, result.Status);
        Assert.Equal(2, result.PassedCount);
        Assert.All(result.Tests, t => Assert.Equal(t.Expected, t.Actual));
    }

    [Fact]
    public async Task Wrong_answer_reports_expected_and_actual_per_test()
    {
        var result = await ExerciseRunner.RunAsync(CSharp(), "int Add(int a, int b) => a * b;");

        Assert.Equal(ExerciseStatus.Failed, result.Status);
        var first = result.Tests[0];
        Assert.False(first.Passed);
        Assert.Equal("3", first.Expected);
        Assert.Equal("2", first.Actual);
        Assert.Equal("0 of 2 tests passed.", result.Summary);
    }

    [Fact]
    public async Task Exceptions_fail_only_their_own_test()
    {
        var q = CSharp(new CodeTest("Div(6, 3)", "2"), new CodeTest("Div(1, 0)", "0"));
        var result = await ExerciseRunner.RunAsync(q, "int Div(int a, int b) => a / b;");

        Assert.True(result.Tests[0].Passed);
        Assert.False(result.Tests[1].Passed);
        Assert.StartsWith("DivideByZeroException", result.Tests[1].Error);
    }

    [Fact]
    public async Task Compile_errors_point_at_the_users_line()
    {
        var result = await ExerciseRunner.RunAsync(CSharp(), "using System.Text;\n\nint Add(int a, int b)\n{\n    return a + c;\n}");

        Assert.Equal(ExerciseStatus.CompileError, result.Status);
        var error = Assert.Single(result.CompileErrors);
        Assert.Equal(5, error.Line);
        Assert.Contains("CS0103", error.Message);
    }

    [Fact]
    public async Task A_misnamed_method_is_reported_against_the_test()
    {
        var result = await ExerciseRunner.RunAsync(CSharp(), "int Sum(int a, int b) => a + b;");

        Assert.Equal(ExerciseStatus.CompileError, result.Status);
        Assert.All(result.CompileErrors, e => Assert.Null(e.Line));
        Assert.Contains(result.CompileErrors, e => e.Message.StartsWith("Test 1 (Add(1, 2)) can't call your code"));
    }

    [Fact]
    public async Task LeetCode_style_Solution_class_with_instance_methods_works()
    {
        var code = "public class Solution\n{\n    public int Add(int a, int b) => a + b;\n}";
        var result = await ExerciseRunner.RunAsync(CSharp(), code);

        Assert.Equal(ExerciseStatus.Passed, result.Status);
    }

    [Fact]
    public async Task Collections_compare_by_content_and_unordered_ignores_order()
    {
        var q = CSharp(
            new CodeTest("Evens(new[] { 1, 2, 3, 4 })", "new List<int> { 2, 4 }"),
            new CodeTest("Evens(new[] { 4, 2 }).Reverse()", "new[] { 4, 2 }", Unordered: true),
            new CodeTest("var r = Evens(new[] { 6 }); return r.Length;", "1"));
        var result = await ExerciseRunner.RunAsync(q, "int[] Evens(int[] xs) => xs.Where(x => x % 2 == 0).ToArray();");

        Assert.True(result.AllPassed, result.Describe());
        Assert.Equal("[2, 4]", result.Tests[0].Actual);
    }

    [Fact]
    public async Task Helpers_are_visible_to_user_code_and_tests()
    {
        var q = CSharp(new CodeTest("ToArray(Reverse(FromArray(new[] { 1, 2, 3 })))", "new[] { 3, 2, 1 }")) with
        {
            Helpers = """
                public class ListNode(int val, ListNode? next = null) { public int val = val; public ListNode? next = next; }
                static ListNode? FromArray(int[] xs) { ListNode? head = null; for (var i = xs.Length - 1; i >= 0; i--) head = new ListNode(xs[i], head); return head; }
                static int[] ToArray(ListNode? n) { var r = new List<int>(); for (; n != null; n = n.next) r.Add(n.val); return r.ToArray(); }
                """,
        };
        const string code = """
            ListNode? Reverse(ListNode? head)
            {
                ListNode? prev = null;
                while (head != null) { var next = head.next; head.next = prev; prev = head; head = next; }
                return prev;
            }
            """;

        var result = await ExerciseRunner.RunAsync(q, code);

        Assert.True(result.AllPassed, result.Describe());
    }

    [Fact]
    public async Task Printed_output_is_kept_separate_from_test_results()
    {
        var result = await ExerciseRunner.RunAsync(CSharp(), "int Add(int a, int b) { Console.WriteLine($\"adding {a}\"); return a + b; }");

        Assert.True(result.AllPassed);
        Assert.Equal("adding 1\nadding -1", result.Output);
    }

    [Fact]
    public async Task An_endless_loop_times_out_without_hanging_the_caller()
    {
        var q = CSharp(new CodeTest("Add(1, 2)", "3"), new CodeTest("Forever()", "0"), new CodeTest("Add(2, 2)", "4"));
        var result = await ExerciseRunner.RunAsync(q, "int Add(int a, int b) => a + b;\nint Forever() { while (true) { } }");

        Assert.Equal(ExerciseStatus.TimedOut, result.Status);
        Assert.True(result.Tests[0].Passed);
        Assert.Equal("timed out", result.Tests[1].Error);
        Assert.Equal("didn't run", result.Tests[2].Error);
        Assert.Contains("test 2", result.Message);
    }

    [Fact]
    public async Task Stack_overflow_is_reported_not_fatal()
    {
        var q = CSharp(new CodeTest("Down(5)", "0"));
        var result = await ExerciseRunner.RunAsync(q, "int Down(int n) => Down(n - 1);");

        Assert.Equal(ExerciseStatus.Crashed, result.Status);
        Assert.Contains("Stack overflow", result.Message);
    }

    // ------------------------------------------------------------------ SQL

    private static Question Sql(string answer) => new()
    {
        Id = "sql-exercises-9001", Type = QuestionType.WriteCode, Language = ExerciseLanguage.Sql, Topic = "sql/exercises",
        Difficulty = 2, Prompt = "Customers with orders.", Explanation = "x", Answer = answer,
        Setup = """
            CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT);
            INSERT INTO Customers VALUES (1, 'Ada'), (2, 'Bo'), (3, 'Cy');
            CREATE TABLE Orders (Id INTEGER PRIMARY KEY, CustomerId INTEGER, Total REAL);
            INSERT INTO Orders VALUES (1, 1, 10), (2, 1, 5), (3, 3, 7);
            """,
    };

    [Fact]
    public async Task Sql_query_with_same_rows_passes_even_with_different_column_names()
    {
        var q = Sql("SELECT c.Name, SUM(o.Total) AS Total FROM Customers c JOIN Orders o ON o.CustomerId = c.Id GROUP BY c.Name ORDER BY c.Name");
        var result = await ExerciseRunner.RunAsync(q, "select Name as who, sum(Total) from Orders join Customers on Customers.Id = CustomerId group by Name order by 1");

        Assert.True(result.AllPassed, result.Describe());
    }

    [Fact]
    public async Task Sql_order_only_matters_when_the_reference_orders()
    {
        var unordered = Sql("SELECT Name FROM Customers WHERE Id IN (SELECT CustomerId FROM Orders)");
        var ordered = Sql("SELECT Name FROM Customers WHERE Id IN (SELECT CustomerId FROM Orders) ORDER BY Name DESC");
        const string mine = "SELECT Name FROM Customers WHERE Id IN (1, 3) ORDER BY Name";

        Assert.True((await ExerciseRunner.RunAsync(unordered, mine)).AllPassed);
        var result = await ExerciseRunner.RunAsync(ordered, mine);
        Assert.False(result.AllPassed);
        Assert.Contains("wrong order", result.Summary);
    }

    [Fact]
    public async Task Sql_wrong_rows_and_errors_are_explained()
    {
        var q = Sql("SELECT Name FROM Customers WHERE Id IN (SELECT CustomerId FROM Orders) ORDER BY Name");

        Assert.Contains("returns 3 rows", (await ExerciseRunner.RunAsync(q, "SELECT Name FROM Customers ORDER BY Name")).Summary);
        Assert.StartsWith("SQL error", (await ExerciseRunner.RunAsync(q, "SELECT Nam FROM Customers")).Summary);
    }

    [Fact]
    public async Task Sql_window_order_by_is_not_a_top_level_order()
    {
        Assert.False(Quizzler.Data.SqlSandbox.HasTopLevelOrderBy("SELECT ROW_NUMBER() OVER (ORDER BY Id) FROM T"));
        Assert.True(Quizzler.Data.SqlSandbox.HasTopLevelOrderBy("SELECT Id FROM (SELECT Id FROM T) ORDER BY Id"));
        Assert.False(Quizzler.Data.SqlSandbox.HasTopLevelOrderBy("SELECT 'order by' FROM T"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Runaway_recursive_cte_is_stopped()
    {
        var q = Sql("SELECT Name FROM Customers ORDER BY Name");
        var result = Quizzler.Data.SqlSandbox.RunExercise(q.Setup!, "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n) SELECT COUNT(*) FROM n", q.Answer, TimeSpan.FromMilliseconds(300));

        Assert.False(result.Passed);
        Assert.Contains("stopped after", result.Mismatch);
        await Task.CompletedTask;
    }
}
