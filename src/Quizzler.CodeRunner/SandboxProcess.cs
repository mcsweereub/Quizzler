using System.Diagnostics;
using System.Text;

namespace Quizzler.CodeRunner;

public sealed record ProcessRun(int ExitCode, string Output, string Error, bool TimedOut, bool OutputTruncated);

/// <summary>
/// Runs a compiled assembly in a separate Quizzler.Sandbox process (shipped next to the app), with a time limit and an
/// output cap. A separate process is the only way to survive user code that loops forever, overflows the stack, or
/// calls Environment.Exit. It is not a security boundary: the code runs as the user, like anything else they compile.
/// </summary>
public static class SandboxProcess
{
    public const int MaxOutputChars = 200_000;
    private const string SandboxName = "Quizzler.Sandbox";

    /// <summary>Where the sandbox was found (exe, or dll run with dotnet), or null if it isn't installed.</summary>
    public static (string FileName, string? DllArgument)? Locate()
    {
        var dir = AppContext.BaseDirectory;
        var exe = Path.Combine(dir, SandboxName + ".exe");
        if (File.Exists(exe)) return (exe, null);
        var dll = Path.Combine(dir, SandboxName + ".dll");
        if (File.Exists(dll)) return (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", dll);
        return null;
    }

    public static async Task<ProcessRun> RunAsync(byte[] assemblyImage, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var sandbox = Locate() ?? throw new InvalidOperationException(
            $"{SandboxName} wasn't found next to the app ({AppContext.BaseDirectory}). Reinstall or rebuild Quizzler.");

        var workDir = Path.Combine(Path.GetTempPath(), "Quizzler", "runs");
        Directory.CreateDirectory(workDir);
        var assemblyPath = Path.Combine(workDir, $"exercise_{Guid.NewGuid():N}.dll");
        await File.WriteAllBytesAsync(assemblyPath, assemblyImage, cancellationToken);

        var start = new ProcessStartInfo(sandbox.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir,
        };
        if (sandbox.DllArgument is { } dll) start.ArgumentList.Add(dll);
        start.ArgumentList.Add(assemblyPath);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            process.StandardInput.Close(); // Console.ReadLine() in user code returns null instead of waiting forever.

            var stdout = ReadCappedAsync(process.StandardOutput);
            var stderr = ReadCappedAsync(process.StandardError);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var (output, truncated) = await stdout;
            var (error, _) = await stderr;
            return new ProcessRun(timedOut ? -1 : process.ExitCode, output, error, timedOut, truncated);
        }
        finally
        {
            if (!process.HasExited) Kill(process);
            TryDelete(assemblyPath);
        }
    }

    /// <summary>Reads a stream to the end, keeping at most <see cref="MaxOutputChars"/> (a print loop can't eat memory).</summary>
    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            var room = MaxOutputChars - sb.Length;
            if (room <= 0) { truncated = true; continue; }
            sb.Append(buffer, 0, Math.Min(read, room));
            if (read > room) truncated = true;
        }
        return (sb.ToString(), truncated);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Still locked by a dying process; the temp folder gets cleaned eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
