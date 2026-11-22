using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Quizzler.CodeRunner;

public sealed record RunResult(bool Compiled, string Output, IReadOnlyList<string> Errors, bool TimedOut = false)
{
    public bool Succeeded => Compiled && !TimedOut && Errors.Count == 0;
}

/// <summary>
/// Compiles a C# snippet written as top-level statements and runs it in-process, capturing console output.
/// Intended for trusted content (verifying question answer keys), not for untrusted user code:
/// a timed-out snippet keeps running on its thread until the process exits.
/// </summary>
public static class CSharpRunner
{
    /// <summary>Usings every snippet gets for free, so authors don't need to write them.</summary>
    public static readonly IReadOnlyList<string> ImplicitUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq",
        "System.Text", "System.Threading", "System.Threading.Tasks",
    ];

    internal static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static readonly SyntaxTree GlobalUsingsTree = CSharpSyntaxTree.ParseText(
        string.Concat(ImplicitUsings.Select(u => $"global using {u};\n")), ParseOptions);

    // Only the core .NET framework assemblies (the folder System.Private.CoreLib lives in), not
    // whatever else the host process happens to have loaded (Roslyn, test frameworks, the app).
    private static readonly Lazy<MetadataReference[]> References = new(() =>
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => string.Equals(Path.GetDirectoryName(path), frameworkDir, StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
    });

    private static readonly Lazy<CSharpCompilation> BaseCompilation = new(() => CSharpCompilation.Create(
        "SnippetBase",
        [GlobalUsingsTree],
        References.Value,
        new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true)));

    // Console output is process-global, so only one snippet can run at a time.
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public static async Task<RunResult> RunAsync(string code, TimeSpan? timeout = null)
    {
        var compile = Compile(code);
        if (compile.Assembly is null)
            return new RunResult(false, "", compile.Errors);

        await RunLock.WaitAsync();
        var originalOut = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var run = Task.Run(() =>
            {
                // Invariant culture so "1.5" doesn't print as "1,5" on some machines.
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
                Invoke(compile.Assembly);
            });

            var finished = await Task.WhenAny(run, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
            if (finished != run)
                return new RunResult(true, writer.ToString(), ["Timed out."], TimedOut: true);

            try
            {
                await run;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                return new RunResult(true, writer.ToString(), [$"Unhandled {inner.GetType().Name}: {inner.Message}"]);
            }

            return new RunResult(true, writer.ToString(), []);
        }
        finally
        {
            Console.SetOut(originalOut);
            RunLock.Release();
        }
    }

    private static (Assembly? Assembly, IReadOnlyList<string> Errors) Compile(string code)
    {
        var (image, errors) = Emit([CSharpSyntaxTree.ParseText(code, ParseOptions)]);
        if (image is null)
            return (null, errors.Select(d => d.ToString()).ToList());

        var context = new AssemblyLoadContext(name: null, isCollectible: true);
        return (context.LoadFromStream(new MemoryStream(image)), []);
    }

    /// <summary>Compiles the trees (plus the implicit global usings) into an executable image, or returns the errors.</summary>
    internal static (byte[]? Image, IReadOnlyList<Diagnostic> Errors) Emit(IEnumerable<SyntaxTree> trees)
    {
        // Deriving from one shared compilation lets Roslyn reuse its bound references between snippets.
        var compilation = BaseCompilation.Value
            .WithAssemblyName($"Snippet_{Guid.NewGuid():N}")
            .AddSyntaxTrees(trees);

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
            return (null, emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList());
        return (ms.ToArray(), []);
    }

    private static void Invoke(Assembly assembly)
    {
        var entry = assembly.EntryPoint ?? throw new InvalidOperationException("Snippet has no entry point.");
        var args = entry.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() };
        var result = entry.Invoke(null, args);
        if (result is Task task) task.GetAwaiter().GetResult();
    }
}
