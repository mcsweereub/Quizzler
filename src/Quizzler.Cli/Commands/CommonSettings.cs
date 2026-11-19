using System.ComponentModel;
using Quizzler.Data;
using Spectre.Console.Cli;

namespace Quizzler.Cli.Commands;

public class DbSettings : CommandSettings
{
    [CommandOption("--db <PATH>")]
    [Description("Database file. Defaults to $QUIZZLER_DB, then %LOCALAPPDATA%\\Quizzler\\quizzler.db.")]
    public string? Db { get; set; }

    public string ResolveDb() =>
        Db ?? Environment.GetEnvironmentVariable("QUIZZLER_DB") ?? QuizDatabase.DefaultPath;
}

public class ContentSettings : DbSettings
{
    [CommandOption("--content <DIR>")]
    [Description("Question bank folder. Defaults to the nearest 'content' folder above the current directory.")]
    public string? Content { get; set; }

    [CommandOption("--no-run")]
    [Description("Skip compiling and running predict-output snippets (faster, less safe).")]
    public bool NoRun { get; set; }

    public string? ResolveContent()
    {
        if (Content is not null)
            return Directory.Exists(Content) ? Path.GetFullPath(Content) : null;

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "content");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
