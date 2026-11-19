using System.IO;

namespace Quizzler.App.Services;

/// <summary>Minimal append-only log next to the database: %LOCALAPPDATA%\Quizzler\quizzler.log.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quizzler", "quizzler.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) info.Delete(); // keep it from growing forever
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}\n");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
