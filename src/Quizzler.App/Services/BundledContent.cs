using System.IO;
using System.Security.Cryptography;
using System.Text;
using Quizzler.Core.Questions;
using Quizzler.Data;

namespace Quizzler.App.Services;

/// <summary>
/// Imports the question bank that ships next to the exe (content\**\*.json, copied at build time).
/// Only runs when the bundled files differ from the last bundle this app imported, so an older
/// build never overwrites content that `quizzler import` brought in from a newer checkout.
/// Snippet execution is skipped here: the test suite verifies content before it ships.
/// </summary>
internal static class BundledContent
{
    private const string FingerprintKey = "app_content_fingerprint";

    public static string? Sync(QuizDatabase db)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "content");
        if (!Directory.Exists(dir)) return null;

        var fingerprint = Fingerprint(dir);
        if (db.GetSetting(FingerprintKey) == fingerprint) return null;

        var load = ContentLoader.LoadDirectory(dir);
        if (!load.IsValid)
        {
            Log.Error("Bundled content has issues; not imported:\n" + string.Join("\n", load.Issues));
            return $"The bundled questions have {load.Issues.Count} problem(s) and weren't imported. See {Log.FilePath}.";
        }

        var report = db.ImportQuestions(load.Questions, DateTimeOffset.Now);
        db.SetSetting(FingerprintKey, fingerprint);
        Log.Info($"Imported bundled content: {report.Added.Count} added, {report.Updated.Count} updated, " +
                 $"{report.Retired.Count} retired, {report.Restored.Count} restored, {report.Unchanged} unchanged.");

        return report.Added.Count > 0 ? $"{report.Added.Count} new question(s) added." : null;
    }

    private static string Fingerprint(string dir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(dir, file).ToLowerInvariant()));
            sha.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
