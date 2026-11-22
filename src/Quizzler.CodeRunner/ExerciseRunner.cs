using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Quizzler.Core.Questions;
using Quizzler.Data;

namespace Quizzler.CodeRunner;

public enum ExerciseStatus
{
    Passed,
    Failed,
    CompileError,
    TimedOut,

    /// <summary>The sandbox process died (stack overflow, Environment.Exit) or couldn't start.</summary>
    Crashed,
}

/// <param name="Line">1-based line in the user's code, or null when the error is in the tests (e.g. a misnamed method).</param>
public sealed record CompileError(int? Line, int? Column, string Message)
{
    public override string ToString() => Line is { } l ? $"Line {l}: {Message}" : Message;
}

/// <param name="Expected">The expected value as printed (or the raw expression, if the test didn't run).</param>
/// <param name="Actual">What the user's code returned, as printed; null if it threw or didn't run.</param>
/// <param name="Error">The exception the user's code threw, or why the test didn't run.</param>
public sealed record TestOutcome(int Index, string Call, string Expected, string? Actual, bool Passed, string? Error, bool Ran);

/// <summary>The result of running a coding exercise once.</summary>
public sealed record ExerciseResult(
    ExerciseStatus Status,
    IReadOnlyList<TestOutcome> Tests,
    IReadOnlyList<CompileError> CompileErrors,
    string Output,
    string? Message,
    SqlExerciseResult? Sql = null)
{
    public bool AllPassed => Status == ExerciseStatus.Passed;
    public int PassedCount => Sql is not null ? (Sql.Passed ? 1 : 0) : Tests.Count(t => t.Passed);
    public int TotalCount => Sql is not null ? 1 : Tests.Count;

    /// <summary>One line for the UI, e.g. "3 of 5 tests passed".</summary>
    public string Summary => Status switch
    {
        ExerciseStatus.Passed when Sql is not null => "Your query returns the expected result.",
        ExerciseStatus.Passed => TotalCount == 1 ? "The test passed." : $"All {TotalCount} tests passed.",
        ExerciseStatus.CompileError => CompileErrors.Count == 1 ? "1 compile error." : $"{CompileErrors.Count} compile errors.",
        ExerciseStatus.TimedOut => Message ?? "Timed out.",
        ExerciseStatus.Crashed => Message ?? "The code crashed.",
        _ when Sql is not null => Sql.Mismatch ?? "Not the expected result.",
        _ => $"{PassedCount} of {TotalCount} tests passed.",
    };

    /// <summary>Plain-text report (for the terminal, and the context sent with "explain my mistake").</summary>
    public string Describe()
    {
        var sb = new StringBuilder(Summary).Append('\n');
        foreach (var e in CompileErrors) sb.Append("  ").Append(e).Append('\n');
        if (Sql is { Actual: { } actual }) sb.Append("Your result:\n").Append(Indent(actual.RowsText)).Append('\n');
        if (Sql is not null) sb.Append("Expected:\n").Append(Indent(Sql.Expected.RowsText)).Append('\n');
        foreach (var t in Tests.Where(t => !t.Passed))
        {
            sb.Append($"  Test {t.Index + 1}: {t.Call}\n    expected {t.Expected}");
            if (t.Actual is not null) sb.Append($", got {t.Actual}");
            if (t.Error is not null) sb.Append(t.Ran ? $", threw {t.Error}" : $" ({t.Error})");
            sb.Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(Output)) sb.Append("Printed:\n").Append(Indent(Output.TrimEnd())).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static string Indent(string s) => string.Join('\n', s.ReplaceLineEndings("\n").Split('\n').Select(l => "    " + l));
}

/// <summary>
/// Grades write-code exercises by running them. C#: the user's code is compiled into a <c>Solution</c> class with
/// generated test calls and run in <see cref="SandboxProcess"/>. SQL: see <see cref="SqlSandbox.RunExercise"/>.
/// </summary>
public static partial class ExerciseRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static Task<ExerciseResult> RunAsync(Question question, string code, CancellationToken cancellationToken = default)
    {
        if (!question.IsExercise) throw new ArgumentException("Not a write-code exercise.", nameof(question));
        return question.Language == ExerciseLanguage.Sql
            ? Task.Run(() => RunSql(question, code), cancellationToken)
            : RunCSharpAsync(question, code, cancellationToken);
    }

    /// <summary>Compiles a trivial program so the first real run isn't slowed by Roslyn warming up (~1-2 s).</summary>
    public static Task WarmUpAsync() => Task.Run(() => CSharpRunner.Emit([CSharpSyntaxTree.ParseText("System.Console.WriteLine(1);", CSharpRunner.ParseOptions)]));

    // ------------------------------------------------------------------ SQL

    private static ExerciseResult RunSql(Question q, string code)
    {
        var sql = SqlSandbox.RunExercise(q.Setup ?? "", code, q.Answer);
        return new ExerciseResult(sql.Passed ? ExerciseStatus.Passed : ExerciseStatus.Failed, [], [], "", sql.Mismatch, sql);
    }

    // ------------------------------------------------------------------ C#

    private const string Marker = "\u001EQZ\t";

    private static async Task<ExerciseResult> RunCSharpAsync(Question q, string code, CancellationToken cancellationToken)
    {
        var build = Build(q, code);
        var (image, diagnostics) = CSharpRunner.Emit(build.Trees);
        if (image is null)
            return new ExerciseResult(ExerciseStatus.CompileError, NotRun(q, "didn't compile"), MapErrors(diagnostics, build, q), "", null);

        ProcessRun run;
        try
        {
            run = await SandboxProcess.RunAsync(image, DefaultTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new ExerciseResult(ExerciseStatus.Crashed, NotRun(q, "didn't run"), [], "", $"Couldn't start the code runner: {ex.Message}");
        }

        var (outcomes, output) = Parse(run.Output, q);
        var ranAll = outcomes.All(o => o.Ran);
        if (run.TimedOut)
        {
            var stuck = outcomes.FirstOrDefault(o => !o.Ran);
            var message = $"Stopped after {DefaultTimeout.TotalSeconds:0} seconds" +
                          (stuck is null ? "." : $" while running test {stuck.Index + 1} (an endless loop?).");
            return new ExerciseResult(ExerciseStatus.TimedOut, MarkStuck(outcomes, "timed out"), [], output, message);
        }
        if (!ranAll || run.ExitCode != 0)
        {
            var error = run.Error.Trim();
            var message = error.Contains("Stack overflow", StringComparison.OrdinalIgnoreCase)
                ? "Stack overflow: the code recursed too deeply (missing base case?)."
                : error.Length > 0 ? $"The code crashed: {FirstLine(error)}" : $"The code stopped early (exit code {run.ExitCode}; Environment.Exit?).";
            return new ExerciseResult(ExerciseStatus.Crashed, MarkStuck(outcomes, "crashed"), [], output, message);
        }

        var status = outcomes.All(o => o.Passed) ? ExerciseStatus.Passed : ExerciseStatus.Failed;
        var note = run.OutputTruncated ? "Output was cut off (too much printing)." : null;
        return new ExerciseResult(status, outcomes, [], output, note);
    }

    private static string FirstLine(string s) => s.ReplaceLineEndings("\n").Split('\n')[0];

    private static List<TestOutcome> NotRun(Question q, string why) =>
        q.Tests.Select((t, i) => new TestOutcome(i, t.Call, t.Expect, null, false, why, Ran: false)).ToList();

    /// <summary>The first test that didn't report is the one that hung or crashed; later ones never started.</summary>
    private static List<TestOutcome> MarkStuck(List<TestOutcome> outcomes, string what)
    {
        var first = true;
        return outcomes.Select(o =>
        {
            if (o.Ran) return o;
            var reason = first ? what : "didn't run";
            first = false;
            return o with { Error = reason };
        }).ToList();
    }

    /// <summary>Splits sandbox stdout into test reports (marker lines) and whatever the user's code printed.</summary>
    private static (List<TestOutcome> Outcomes, string Output) Parse(string stdout, Question q)
    {
        var outcomes = NotRun(q, "didn't run");
        var userLines = new List<string>();
        foreach (var line in stdout.ReplaceLineEndings("\n").Split('\n'))
        {
            var at = line.IndexOf(Marker, StringComparison.Ordinal);
            if (at < 0)
            {
                userLines.Add(line);
                continue;
            }
            if (at > 0) userLines.Add(line[..at]);
            else if (userLines.Count > 0 && userLines[^1].Length == 0) userLines.RemoveAt(userLines.Count - 1); // the harness's own line break

            var fields = line[(at + Marker.Length)..].Split('\t');
            if (fields.Length < 5 || !int.TryParse(fields[0], out var index) || index < 0 || index >= outcomes.Count) continue;
            var test = q.Tests[index];
            var (status, actual, expected, error) = (fields[1], Unescape(fields[2]), Unescape(fields[3]), Unescape(fields[4]));
            outcomes[index] = new TestOutcome(index, test.Call,
                Expected: expected.Length > 0 ? expected : test.Expect,
                Actual: status == "pass" || (status == "fail" && error.Length == 0) ? actual : null,
                Passed: status == "pass",
                Error: error.Length > 0 ? error : null,
                Ran: true);
        }
        return (outcomes, string.Join('\n', userLines).Trim('\n'));
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length) { sb.Append(s[i]); continue; }
            sb.Append(s[++i] switch { 't' => '\t', 'n' => '\n', 'r' => '\r', var c => c });
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ building the program

    private sealed record BuildResult(IReadOnlyList<SyntaxTree> Trees, SyntaxTree User, SyntaxTree Tests, SyntaxTree? Helpers, IReadOnlyDictionary<int, int> TestByLine);

    [GeneratedRegex(@"^\s*using\s+(static\s+)?[\w.]+(\s*=\s*[\w.<>, ]+)?\s*;\s*$")]
    private static partial Regex UsingLine();

    [GeneratedRegex(@"\breturn\b")]
    private static partial Regex ReturnKeyword();

    [GeneratedRegex(@"\bclass\s+Solution\b")]
    private static partial Regex SolutionClass();

    [GeneratedRegex(@"\bstatic\s+(?:partial\s+)?class\s+Solution\b")]
    private static partial Regex StaticSolutionClass();

    [GeneratedRegex(@"(?<!\bpartial\s+)\bclass\s+Solution\b")]
    private static partial Regex NonPartialSolutionClass();

    /// <summary>
    /// User code goes inside <c>partial class Solution</c> (unless they wrote that class themselves, LeetCode style), with
    /// its line numbers unchanged so compile errors point at the right line. Tests run in another part of the same class,
    /// so they can call private and instance methods.
    /// </summary>
    private static BuildResult Build(Question q, string code)
    {
        // Leading usings must sit outside the class: move them onto the first line, blanking their own lines.
        var lines = code.ReplaceLineEndings("\n").Split('\n');
        var usings = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (!UsingLine().IsMatch(lines[i])) break;
            usings.Add(lines[i].Trim());
            lines[i] = "";
        }
        var body = string.Join('\n', lines);
        var prefix = string.Join(' ', usings);

        var ownClass = SolutionClass().IsMatch(body);
        var isStatic = ownClass && StaticSolutionClass().IsMatch(body);
        var userText = ownClass
            ? prefix + " " + NonPartialSolutionClass().Replace(body, "partial class Solution", 1)
            : prefix + " partial class Solution { " + body + "\n}";

        var tests = new StringBuilder();
        var testByLine = new Dictionary<int, int>();
        tests.Append(isStatic ? "Solution.__QuizzlerRunTests();\n" : "new Solution().__QuizzlerRunTests();\n");
        tests.Append("partial class Solution\n{\n");
        tests.Append(isStatic ? "    internal static void __QuizzlerRunTests()\n    {\n" : "    internal void __QuizzlerRunTests()\n    {\n");
        var line = 5;
        for (var i = 0; i < q.Tests.Count; i++)
        {
            var t = q.Tests[i];
            var call = ReturnKeyword().IsMatch(t.Call) ?$"() => {{ {t.Call} }}" : $"() => (object?)({t.Call})";
            tests.Append($"        QuizzlerHarness.Run({i}, {call}, () => (object?)({t.Expect}), {(t.Unordered ? "true" : "false")});\n");
            testByLine[line++] = i;
        }
        tests.Append("    }\n}\n");
        tests.Append(HarnessSource);

        var userTree = CSharpSyntaxTree.ParseText(userText, CSharpRunner.ParseOptions, path: "Solution.cs");
        var testTree = CSharpSyntaxTree.ParseText(tests.ToString(), CSharpRunner.ParseOptions, path: "Tests.cs");
        var helperTree = string.IsNullOrWhiteSpace(q.Helpers)
            ? null
            : CSharpSyntaxTree.ParseText("partial class Solution {\n" + q.Helpers + "\n}", CSharpRunner.ParseOptions, path: "Helpers.cs");

        List<SyntaxTree> trees = [userTree, testTree];
        if (helperTree is not null) trees.Add(helperTree);
        return new BuildResult(trees, userTree, testTree, helperTree, testByLine);
    }

    private static List<CompileError> MapErrors(IReadOnlyList<Diagnostic> diagnostics, BuildResult build, Question q)
    {
        var errors = new List<CompileError>();
        foreach (var d in diagnostics.Take(20))
        {
            var span = d.Location.GetLineSpan();
            var message = $"{d.Id}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}";
            var tree = d.Location.SourceTree;
            if (tree == build.User)
            {
                var line = span.StartLinePosition.Line + 1;
                // The first line carries the moved usings and the class header; columns there are approximate.
                errors.Add(new CompileError(line, line == 1 ? null : span.StartLinePosition.Character + 1, message));
            }
            else if (tree == build.Tests && build.TestByLine.TryGetValue(span.StartLinePosition.Line, out var index))
            {
                errors.Add(new CompileError(null, null, $"Test {index + 1} ({q.Tests[index].Call}) can't call your code: {message}"));
            }
            else if (tree == build.Helpers)
            {
                errors.Add(new CompileError(null, null, $"In the provided helpers: {message}"));
            }
            else
            {
                errors.Add(new CompileError(null, null, message));
            }
        }
        // A broken user file often also breaks every test call; the user-code errors are the ones to fix first.
        return errors.Any(e => e.Line is not null) ? errors.Where(e => e.Line is not null).ToList() : errors.Distinct().ToList();
    }

    /// <summary>Runs one test and prints a marker line: index, pass/fail/error, actual, expected, exception.</summary>
    private const string HarnessSource = """
        static class QuizzlerHarness
        {
            public static void Run(int index, System.Func<object?> actual, System.Func<object?> expected, bool unordered)
            {
                string expectedText;
                try { expectedText = Format(expected(), unordered); }
                catch (System.Exception ex) { Report(index, "error", "", "", "the test's expected value threw " + ex.GetType().Name + ": " + ex.Message); return; }
                try
                {
                    var actualText = Format(actual(), unordered);
                    Report(index, actualText == expectedText ? "pass" : "fail", actualText, expectedText, "");
                }
                catch (System.Exception ex)
                {
                    Report(index, "fail", "", expectedText, ex.GetType().Name + ": " + ex.Message);
                }
            }

            static void Report(int index, string status, string actual, string expected, string error)
            {
                var o = System.Console.Out;
                o.Flush();
                o.Write("\n\u001EQZ\t" + index + "\t" + status + "\t" + Escape(Cap(actual)) + "\t" + Escape(Cap(expected)) + "\t" + Escape(Cap(error)) + "\n");
                o.Flush();
            }

            static string Cap(string s) => s.Length <= 2000 ? s : s.Substring(0, 2000) + "…";

            static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

            public static string Format(object? value, bool unordered = false)
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                switch (value)
                {
                    case null: return "null";
                    case string s: return "\"" + s + "\"";
                    case char c: return "'" + c + "'";
                    case bool b: return b ? "true" : "false";
                    case double d: return d.ToString("R", inv);
                    case float f: return f.ToString("R", inv);
                    case System.Collections.IDictionary dict:
                    {
                        var entries = new List<string>();
                        foreach (System.Collections.DictionaryEntry e in dict) entries.Add(Format(e.Key) + ": " + Format(e.Value));
                        entries.Sort(System.StringComparer.Ordinal);
                        return "{" + string.Join(", ", entries) + "}";
                    }
                    case System.Collections.IEnumerable items:
                    {
                        var parts = new List<string>();
                        foreach (var item in items)
                        {
                            if (parts.Count == 10000) { parts.Add("…"); break; }
                            parts.Add(Format(item));
                        }
                        if (unordered) parts.Sort(System.StringComparer.Ordinal);
                        return "[" + string.Join(", ", parts) + "]";
                    }
                    case System.Runtime.CompilerServices.ITuple t:
                    {
                        var parts = new List<string>();
                        for (var i = 0; i < t.Length; i++) parts.Add(Format(t[i]));
                        return "(" + string.Join(", ", parts) + ")";
                    }
                    case System.IFormattable formattable: return formattable.ToString(null, inv);
                    default: return value.ToString() ?? "";
                }
            }
        }
        """;
}
