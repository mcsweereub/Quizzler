using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;

namespace Quizzler.Core.Study;

/// <summary>Persistence the study loop needs. Implemented by Quizzler.Data (SQLite).</summary>
public interface IQuizStore
{
    /// <summary>Questions currently in the content bank (excludes retired ones).</summary>
    IReadOnlyList<Question> GetActiveQuestions();

    IReadOnlyList<CardState> GetCards();

    /// <summary>Saves the updated card and appends the review log, atomically.</summary>
    void SaveReview(CardState card, ReviewLog log);

    /// <summary>Review history from <paramref name="since"/> onwards, oldest first.</summary>
    IReadOnlyList<ReviewLog> GetReviewsSince(DateTimeOffset since);

    StudySettings GetStudySettings();

    void FlagQuestion(string questionId, string? note, DateTimeOffset now);

    /// <summary>Appends a usage event (see <see cref="StudyEvent"/>).</summary>
    void LogEvent(string kind, string? detail, DateTimeOffset at);

    IReadOnlyList<StudyEventLog> GetEventsSince(DateTimeOffset since);
}
