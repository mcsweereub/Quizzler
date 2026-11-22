using System.Diagnostics;
using Quizzler.Core.Grading;
using Quizzler.Core.Scheduling;

namespace Quizzler.Core.Study;

public enum AnswerState
{
    Correct,
    Incorrect,

    /// <summary>Show the real answer and let the user mark themselves.</summary>
    NeedsSelfGrade,
}

/// <param name="CanOverride">A typed answer the text matcher rejected; the user may say they were actually right.</param>
public sealed record PendingAnswer(AnswerState State, string Response, bool CanOverride);

/// <summary>
/// One sitting of questions, independent of any UI: walk the plan, build multiple-choice options,
/// grade answers, and record outcomes through <see cref="QuizService"/>.
/// Flow per question: <see cref="MoveNext"/> → Submit* → <see cref="Complete"/>.
/// </summary>
public sealed class StudySession
{
    private readonly QuizService _service;
    private readonly Random _random;
    private readonly IReadOnlyList<SessionItem> _items;
    private readonly Stopwatch _stopwatch = new();
    private int _index = -1;

    private bool _ended;

    public StudySession(QuizService service, SessionPlan plan, Random? random = null, SessionFilter? filter = null)
    {
        _service = service;
        _items = plan.Items;
        _random = random ?? new Random();
        Filter = filter ?? SessionFilter.Everything;
        if (_items.Count > 0)
        {
            var detail = $"{plan.DueReviews} reviews, {plan.NewCards} new";
            if (!Filter.IsEverything) detail += $"; filter {Filter.Serialize()}";
            _service.LogEvent(StudyEvent.SessionStarted, detail);
        }
        else
        {
            _ended = true;
        }
    }

    public SessionFilter Filter { get; }

    /// <summary>Logs the end of the session (once). Called automatically when the last question is done; call it if the user quits early.</summary>
    public void End()
    {
        if (_ended) return;
        _ended = true;
        _service.LogEvent(StudyEvent.SessionEnded, $"{Answered}/{Total} answered, {CorrectCount} correct");
    }

    public int Total => _items.Count;

    /// <summary>1-based position of the current question.</summary>
    public int Position => Math.Min(_index + 1, Total);

    public SessionItem? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;
    public MultipleChoiceOptions? Choices { get; private set; }
    public PendingAnswer? Pending { get; private set; }
    public int Answered { get; private set; }
    public int CorrectCount { get; private set; }
    public bool IsFinished => _index >= _items.Count;

    /// <summary>Advances to the next question. Returns false when the session is over.</summary>
    public bool MoveNext()
    {
        if (Pending is not null) throw new InvalidOperationException("Complete the current answer first.");
        if (IsFinished) return false;

        _index++;
        Choices = null;
        if (Current is not { } item)
        {
            End();
            return false;
        }

        if (item.Stage == CardStage.MultipleChoice && !item.Question.IsExercise)
            Choices = MultipleChoice.Build(item.Question, _random);
        _stopwatch.Restart();
        return true;
    }

    public PendingAnswer SubmitChoice(int index)
    {
        var item = RequireUnanswered();
        if (Choices is null) throw new InvalidOperationException("Current question is not multiple choice.");
        if (index < 0 || index >= Choices.Options.Count) throw new ArgumentOutOfRangeException(nameof(index));

        _stopwatch.Stop();
        var state = Choices.IsCorrect(index) ? AnswerState.Correct : AnswerState.Incorrect;
        return Pending = new PendingAnswer(state, Choices.Options[index], CanOverride: false);
    }

    public PendingAnswer SubmitTyped(string text)
    {
        var item = RequireUnanswered();
        if (item.Question.IsExercise) throw new InvalidOperationException("Exercises are graded by running them; use SubmitExercise.");
        if (item.Stage != CardStage.Typed) throw new InvalidOperationException("Current question is multiple choice.");

        _stopwatch.Stop();
        var state = AnswerChecker.Check(item.Question, text) switch
        {
            CheckResult.Correct => AnswerState.Correct,
            CheckResult.Incorrect => AnswerState.Incorrect,
            _ => AnswerState.NeedsSelfGrade,
        };
        return Pending = new PendingAnswer(state, text, CanOverride: state == AnswerState.Incorrect);
    }

    /// <summary>
    /// Submits a coding exercise after the front end has run it. <paramref name="passed"/>: every test passed
    /// (false for "give up"). The timer covers every attempt, so time spent on failed runs counts.
    /// </summary>
    public PendingAnswer SubmitExercise(string code, bool passed)
    {
        var item = RequireUnanswered();
        if (!item.Question.IsExercise) throw new InvalidOperationException("Current question is not a coding exercise.");

        _stopwatch.Stop();
        return Pending = new PendingAnswer(passed ? AnswerState.Correct : AnswerState.Incorrect, code, CanOverride: false);
    }

    /// <summary>Records the final outcome for the current question and returns the updated card.</summary>
    public CardState Complete(AnswerOutcome outcome)
    {
        var item = Current ?? throw new InvalidOperationException("No current question.");
        var pending = Pending ?? throw new InvalidOperationException("Submit an answer first.");

        if (pending is { State: AnswerState.Incorrect, CanOverride: false } && outcome != AnswerOutcome.Wrong)
            throw new InvalidOperationException("A wrong multiple-choice answer can't be marked right.");

        var grade = pending.State switch
        {
            AnswerState.Correct => ReviewGrade.Correct,
            AnswerState.Incorrect => ReviewGrade.Incorrect,
            _ => ReviewGrade.SelfGraded,
        };
        var card = _service.RecordAnswer(item, outcome, pending.Response, _stopwatch.Elapsed, grade);
        Answered++;
        if (outcome != AnswerOutcome.Wrong) CorrectCount++;
        Pending = null;
        return card;
    }

    private SessionItem RequireUnanswered()
    {
        var item = Current ?? throw new InvalidOperationException("Call MoveNext first.");
        if (Pending is not null) throw new InvalidOperationException("Already answered; call Complete.");
        return item;
    }
}
