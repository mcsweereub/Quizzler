using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quizzler.Core.Questions;

public sealed record LoadedQuestion(Question Question, string SourceFile);

public sealed record ContentIssue(string SourceFile, string? QuestionId, string Message)
{
    public override string ToString() =>
        QuestionId is null ? $"{SourceFile}: {Message}" : $"{SourceFile} [{QuestionId}]: {Message}";
}

public sealed record ContentLoadResult(IReadOnlyList<LoadedQuestion> Questions, IReadOnlyList<ContentIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>Reads question JSON files and runs them through <see cref="ContentValidator"/>.</summary>
public static class ContentLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
        // Catch typos like "distractor" instead of silently ignoring them.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static ContentLoadResult LoadDirectory(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase);

        var questions = new List<LoadedQuestion>();
        var issues = new List<ContentIssue>();

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            try
            {
                using var stream = File.OpenRead(file);
                var parsed = JsonSerializer.Deserialize<QuestionFile>(stream, JsonOptions);
                if (parsed is null)
                {
                    issues.Add(new(relative, null, "File is empty or null."));
                    continue;
                }
                questions.AddRange(parsed.Questions.Select(q => new LoadedQuestion(q, relative)));
            }
            catch (JsonException ex)
            {
                issues.Add(new(relative, null, $"Invalid JSON: {ex.Message}"));
            }
        }

        issues.AddRange(ContentValidator.Validate(questions));
        return new ContentLoadResult(questions, issues);
    }
}
