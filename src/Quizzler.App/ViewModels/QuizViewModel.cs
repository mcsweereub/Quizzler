using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;

namespace Quizzler.App.ViewModels;

public enum QuizMode
{
    /// <summary>Waiting for an answer.</summary>
    Question,

    /// <summary>Answered: showing right/wrong, explanation, and follow-up actions.</summary>
    Feedback,

    Finished,

    /// <summary>Nothing was due when the window opened.</summary>
    Empty,
}

/// <summary>Everything about the current question that doesn't change while it's on screen.</summary>
public sealed record QuestionView(
    string PositionText,
    double Progress,
    string Topic,
    string DifficultyText,
    string StageText,
    bool IsNew,
    string Prompt,
    string? Code,
    bool IsMultipleChoice,
    bool IsMultiLineAnswer,
    string TypedHint)
{
    public bool HasCode => !string.IsNullOrWhiteSpace(Code);
    public bool IsTyped => !IsMultipleChoice;
    public bool IsSingleLineAnswer => IsTyped && !IsMultiLineAnswer;
    public bool IsMultiLineTyped => IsTyped && IsMultiLineAnswer;
    public string KeyHint => IsMultipleChoice ? "Press 1-4 or click an option · Esc to close"
        : IsMultiLineAnswer ? "Ctrl+Enter to submit · Esc to close"
        : "Enter to submit · Esc to close";
}

/// <summary>Result panel shown after answering.</summary>
public sealed record FeedbackView(AnswerState State, string Answer, bool AnswerIsCode, bool ShowAnswer, string Explanation, bool CanOverride)
{
    public bool IsCorrect => State == AnswerState.Correct;
    public bool IsIncorrect => State == AnswerState.Incorrect;
    public bool NeedsSelfGrade => State == AnswerState.NeedsSelfGrade;
    public string Title => State switch
    {
        AnswerState.Correct => "✔  Correct",
        AnswerState.Incorrect => "✘  Not quite",
        _ => "How did you do?",
    };
    public bool ShowAnswerAsText => ShowAnswer && !AnswerIsCode;
    public bool ShowAnswerAsCode => ShowAnswer && AnswerIsCode;
    public bool ShowNext => !NeedsSelfGrade;
    public bool ShowOverride => IsIncorrect && CanOverride;
}

public enum OptionState { Normal, Correct, Wrong, Dimmed }

public sealed partial class OptionViewModel(int index, string text, bool isCode) : ObservableObject
{
    public int Index { get; } = index;
    public string Number { get; } = (index + 1).ToString();
    public string Text { get; } = text;
    public bool IsCode { get; } = isCode;
    public bool IsPlain => !IsCode;

    private OptionState _state;
    public OptionState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }
}

public sealed class QuizViewModel : ObservableObject
{
    private readonly QuizService _service;
    private readonly StudySession _session;

    private QuizMode _mode;
    private QuestionView? _question;
    private FeedbackView? _feedback;
    private string _typedAnswer = "";
    private bool _isFlagging;
    private string _flagNote = "";
    private bool _flagged;
    private string _summaryText = "";
    private string _nextReviewText = "";

    public QuizViewModel(QuizService service, Action? openSettings = null)
    {
        OpenSettingsCommand = new RelayCommand(() => openSettings?.Invoke());
        _service = service;
        var plan = service.PlanSession();
        _session = new StudySession(service, plan);
        SessionDescription = plan.IsEmpty ? "" : $"{plan.DueReviews} to review · {plan.NewCards} new";

        ChooseCommand = new RelayCommand<OptionViewModel>(o => { if (o is not null) Choose(o.Index); });
        SubmitCommand = new RelayCommand(Submit);
        NextCommand = new RelayCommand(Next);
        GuessedCommand = new RelayCommand(() => CompleteIf(AnswerState.Correct, AnswerOutcome.Guessed));
        EasyCommand = new RelayCommand(() => CompleteIf(AnswerState.Correct, AnswerOutcome.Easy));
        OverrideCommand = new RelayCommand(Override);
        SelfRightCommand = new RelayCommand(() => CompleteIf(AnswerState.NeedsSelfGrade, AnswerOutcome.Right));
        SelfWrongCommand = new RelayCommand(() => CompleteIf(AnswerState.NeedsSelfGrade, AnswerOutcome.Wrong));
        StartFlagCommand = new RelayCommand(StartFlag);
        SaveFlagCommand = new RelayCommand(SaveFlag);
        CancelFlagCommand = new RelayCommand(() => IsFlagging = false);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        Advance();
    }

    public event EventHandler? CloseRequested;

    public string SessionDescription { get; }
    public ObservableCollection<OptionViewModel> Options { get; } = [];

    public RelayCommand<OptionViewModel> ChooseCommand { get; }
    public RelayCommand SubmitCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand GuessedCommand { get; }
    public RelayCommand EasyCommand { get; }
    public RelayCommand OverrideCommand { get; }
    public RelayCommand SelfRightCommand { get; }
    public RelayCommand SelfWrongCommand { get; }
    public RelayCommand StartFlagCommand { get; }
    public RelayCommand SaveFlagCommand { get; }
    public RelayCommand CancelFlagCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }

    public QuizMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(ShowQuestion));
            OnPropertyChanged(nameof(IsAnswering));
            OnPropertyChanged(nameof(ShowFeedback));
            OnPropertyChanged(nameof(ShowFinished));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(IsDone));
        }
    }

    public bool ShowQuestion => Mode is QuizMode.Question or QuizMode.Feedback;
    public bool IsAnswering => Mode == QuizMode.Question;
    public bool ShowFeedback => Mode == QuizMode.Feedback;
    public bool ShowFinished => Mode == QuizMode.Finished;
    public bool ShowEmpty => Mode == QuizMode.Empty;
    public bool IsDone => Mode is QuizMode.Finished or QuizMode.Empty;

    public QuestionView? Question
    {
        get => _question;
        private set => SetProperty(ref _question, value);
    }

    public FeedbackView? Feedback
    {
        get => _feedback;
        private set => SetProperty(ref _feedback, value);
    }

    public string TypedAnswer
    {
        get => _typedAnswer;
        set => SetProperty(ref _typedAnswer, value);
    }

    public bool IsFlagging
    {
        get => _isFlagging;
        private set
        {
            if (SetProperty(ref _isFlagging, value)) OnPropertyChanged(nameof(CanStartFlag));
        }
    }

    public string FlagNote
    {
        get => _flagNote;
        set => SetProperty(ref _flagNote, value);
    }

    public bool Flagged
    {
        get => _flagged;
        private set
        {
            if (SetProperty(ref _flagged, value)) OnPropertyChanged(nameof(CanStartFlag));
        }
    }

    public bool CanStartFlag => !IsFlagging && !Flagged;

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string NextReviewText
    {
        get => _nextReviewText;
        private set => SetProperty(ref _nextReviewText, value);
    }

    // ------------------------------------------------------------------ actions

    public void Choose(int index)
    {
        if (Mode != QuizMode.Question || _session.Choices is not { } choices || index < 0 || index >= choices.Options.Count) return;

        var pending = _session.SubmitChoice(index);
        foreach (var option in Options)
        {
            option.State = option.Index == choices.CorrectIndex ? OptionState.Correct
                : option.Index == index ? OptionState.Wrong
                : OptionState.Dimmed;
        }
        ShowResult(pending, showAnswer: false);
    }

    public void Submit()
    {
        if (Mode != QuizMode.Question || Question is not { IsTyped: true } || string.IsNullOrWhiteSpace(TypedAnswer)) return;
        ShowResult(_session.SubmitTyped(TypedAnswer.TrimEnd()), showAnswer: true);
    }

    /// <summary>Accept the default outcome: right if correct, wrong if not.</summary>
    public void Next()
    {
        if (Mode != QuizMode.Feedback || Feedback is null || Feedback.NeedsSelfGrade) return;
        Complete(Feedback.IsCorrect ? AnswerOutcome.Right : AnswerOutcome.Wrong);
    }

    public void Override()
    {
        if (Feedback is { ShowOverride: true }) Complete(AnswerOutcome.Right);
    }

    public void CompleteIf(AnswerState requiredState, AnswerOutcome outcome)
    {
        if (Mode == QuizMode.Feedback && Feedback?.State == requiredState) Complete(outcome);
    }

    public void StartFlag()
    {
        if (Mode != QuizMode.Feedback || !CanStartFlag) return;
        FlagNote = "";
        IsFlagging = true;
    }

    public void SaveFlag()
    {
        if (!IsFlagging || _session.Current is not { } item) return;
        _service.Flag(item.Question.Id, FlagNote);
        IsFlagging = false;
        Flagged = true;
    }

    public void CancelFlag() => IsFlagging = false;

    /// <summary>Called when the window closes: an answered-but-unconfirmed question still counts.</summary>
    public void CompletePendingOnClose()
    {
        if (Mode == QuizMode.Feedback && Feedback is { NeedsSelfGrade: false }) Next();
    }

    // ------------------------------------------------------------------ flow

    private void ShowResult(PendingAnswer pending, bool showAnswer)
    {
        var q = _session.Current!.Question;
        var answerIsCode = q.Type != QuestionType.Concept || q.Answer.Contains('\n');
        // Typed answers that weren't right show the real answer. MC highlights it in the options instead.
        var revealAnswer = (showAnswer && pending.State != AnswerState.Correct) || pending.State == AnswerState.NeedsSelfGrade;
        Feedback = new FeedbackView(pending.State, q.Answer, answerIsCode, revealAnswer, q.Explanation, pending.CanOverride);
        Mode = QuizMode.Feedback;
    }

    private void Complete(AnswerOutcome outcome)
    {
        _session.Complete(outcome);
        Advance();
    }

    private void Advance()
    {
        IsFlagging = false;
        Flagged = false;
        Feedback = null;
        TypedAnswer = "";
        Options.Clear();

        if (!_session.MoveNext())
        {
            Finish();
            return;
        }

        var item = _session.Current!;
        var q = item.Question;
        var isMc = item.Stage == CardStage.MultipleChoice;
        var multiLine = q.Type != QuestionType.Concept || q.EffectiveRecall == RecallGrading.Self;
        var hint = q.Type switch
        {
            QuestionType.PredictOutput => "Type the exact output. Ctrl+Enter to submit.",
            QuestionType.PickTheCode => "Write the code. You'll compare it with the answer. Ctrl+Enter to submit.",
            _ when q.EffectiveRecall == RecallGrading.Self => "Answer in your own words. You'll compare with the answer. Ctrl+Enter to submit.",
            _ => "Type your answer and press Enter.",
        };

        Question = new QuestionView(
            PositionText: $"{_session.Position} / {_session.Total}",
            Progress: (double)(_session.Position - 1) / _session.Total,
            Topic: q.Topic,
            DifficultyText: $"Difficulty {q.Difficulty}",
            StageText: isMc ? "Multiple choice" : "Type the answer",
            IsNew: item.IsNew,
            Prompt: q.Prompt,
            Code: q.Code,
            IsMultipleChoice: isMc,
            IsMultiLineAnswer: multiLine,
            TypedHint: hint);

        if (_session.Choices is { } choices)
        {
            for (var i = 0; i < choices.Options.Count; i++)
            {
                var text = choices.Options[i];
                Options.Add(new OptionViewModel(i, text, q.Type == QuestionType.PickTheCode || text.Contains('\n')));
            }
        }

        Mode = QuizMode.Question;
    }

    private void Finish()
    {
        var summary = _service.GetDueSummary();
        var now = _service.Now;
        NextReviewText = summary.Total > 0
            ? $"{summary.Total} more ready now. Open Quizzler again to keep going."
            : summary.NextDueAt is { } next
                ? $"Next review {TimeText.Until(next - now)}. Quizzler will remind you."
                : "Add more questions to keep learning.";

        if (_session.Total == 0)
        {
            SummaryText = "All caught up!";
            Mode = QuizMode.Empty;
            return;
        }

        var pct = _session.Answered == 0 ? 0 : 100 * _session.CorrectCount / _session.Answered;
        SummaryText = $"{_session.CorrectCount} of {_session.Answered} correct ({pct}%)";
        Mode = QuizMode.Finished;
    }
}
