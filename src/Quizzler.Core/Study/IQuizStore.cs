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

    StudySettings GetStudySettings();

    void FlagQuestion(string questionId, string? note, DateTimeOffset now);
}
