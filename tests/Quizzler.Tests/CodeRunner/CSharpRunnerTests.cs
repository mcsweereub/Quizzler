using Quizzler.CodeRunner;

namespace Quizzler.Tests.CodeRunner;

public class CSharpRunnerTests
{
    [Fact]
    public async Task Captures_console_output()
    {
        var result = await CSharpRunner.RunAsync("Console.WriteLine(\"hello\"); Console.Write(1 + 2);");
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal("hello" + Environment.NewLine + "3", result.Output);
    }

    [Fact]
    public async Task Supports_await_and_type_declarations()
    {
        var code = """
            await Task.Delay(1);
            Console.WriteLine(new Point(1, 2));
            record Point(int X, int Y);
            """;
        var result = await CSharpRunner.RunAsync(code);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal("Point { X = 1, Y = 2 }", result.Output.Trim());
    }

    [Fact]
    public async Task Uses_invariant_culture()
    {
        var result = await CSharpRunner.RunAsync("Console.Write(1.5);");
        Assert.Equal("1.5", result.Output);
    }

    [Fact]
    public async Task Reports_compile_errors()
    {
        var result = await CSharpRunner.RunAsync("int x = \"nope\";");
        Assert.False(result.Compiled);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Reports_unhandled_exceptions_with_output_so_far()
    {
        var result = await CSharpRunner.RunAsync("Console.Write(\"before\"); throw new InvalidOperationException(\"boom\");");
        Assert.False(result.Succeeded);
        Assert.Equal("before", result.Output);
        Assert.Contains("InvalidOperationException", result.Errors[0]);
    }

    [Fact]
    public async Task Times_out_infinite_loops()
    {
        var result = await CSharpRunner.RunAsync("while (true) { }", TimeSpan.FromMilliseconds(300));
        Assert.True(result.TimedOut);
    }
}
