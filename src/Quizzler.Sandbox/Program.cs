using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

// Quizzler.Sandbox <assembly.dll>
//
// Runs a compiled exercise's entry point in a separate process, so the user's code can loop forever, overflow the
// stack or call Environment.Exit without taking the app down. The parent (Quizzler.CodeRunner.SandboxProcess)
// enforces the time limit by killing this process, and reads test results from stdout.
//
// Exit codes: 0 finished, 2 bad arguments, 3 unhandled exception (message on stderr).

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: Quizzler.Sandbox <assembly.dll>");
    return 2;
}

Console.OutputEncoding = new UTF8Encoding(false);
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

try
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
    var entry = assembly.EntryPoint ?? throw new InvalidOperationException("The compiled code has no entry point.");
    var result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : [Array.Empty<string>()]);
    if (result is Task task) task.GetAwaiter().GetResult();
    Console.Out.Flush();
    return 0;
}
catch (Exception ex)
{
    var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
    Console.Out.Flush();
    Console.Error.WriteLine($"{inner.GetType().Name}: {inner.Message}");
    return 3;
}
