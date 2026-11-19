using System.Text.Json.Serialization;

namespace Quizzler.Core.Questions;

/// <summary>Shape of a single JSON file in the content/ folder.</summary>
public sealed record QuestionFile
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public IReadOnlyList<Question> Questions { get; init; } = [];
}
