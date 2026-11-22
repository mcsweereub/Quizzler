using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quizzler.Ai;
using Quizzler.CodeRunner;
using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;
using Quizzler.App.Services;
using Quizzler.Data;

namespace Quizzler.App.ViewModels;

public enum QuizMode
{
    /// <summary>Choosing what to study.</summary>
    Picker,

    /// <summary>Waiting for an answer.</summary>
    Question,

    /// <summary>Answered: showing right/wrong, explanation, and follow-up actions.</summary>
    Feedback,

    Finished,

    /// <summary>Nothing was due when the session started.</summary>
    Empty,
}

/// <summary>What the quiz window needs from the app, beyond the study loop itself.</summary>
public sealed record QuizOptions
{
    /// <summary>Open on "choose what to study" instead of starting straight away.</summary>
    public bool StartWithPicker { get; init; }

    /// <summary>The session to start (or the picker's initial choice).</summary>
    public SessionFilter Filter { get; init; } = SessionFilter.Everything;

    /// <summary>Called when the user starts a session from the picker (the app remembers it for next time).</summary>
    public Action<SessionFilter>? FilterChosen { get; init; }

    public Action? OpenSettings { get; init; }
    public Action? OpenStats { get; init; }

    /// <summary>The Anthropic API key for "explain my mistake", or null when none is set up.</summary>
    public Func<string?> GetApiKey { get; init; } = () => null;

    public Func<string, IMistakeExplainer> CreateExplainer { get; init; } = key => new ClaudeMistakeExplainer(key);

    /// <summary>Unfinished exercise code, kept per question id (null clears it).</summary>
    public Func<string, string?> LoadDraft { get; init; } = _ => null;
    public Action<string, string?> SaveDraft { get; init; } = (_, _) => { };
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
    CodeLanguage CodeSyntax,
    bool IsMultipleChoice,
    bool IsMultiLineAnswer,
    string TypedHint,
    bool IsExercise = false,
    string? CodeCaption = null)
{
    public bool HasCode => !string.IsNullOrWhiteSpace(Code);
    public bool HasCodeCaption => HasCode && CodeCaption is not null;
    public bool IsTyped => !IsMultipleChoice && !IsExercise;
    public bool IsSingleLineAnswer => IsTyped && !IsMultiLineAnswer;
    public bool IsMultiLineTyped => IsTyped && IsMultiLineAnswer;
    public string KeyHint => IsExercise ? "Ctrl+Enter to run · your code is kept if you close the window"
        : IsMultipleChoice ? "Press 1-4 or click an option · Esc to close"
        : IsMultiLineAnswer ? "Ctrl+Enter to submit · Esc to close"
        : "Enter to submit · Esc to close";
}

/// <summary>Result panel shown after answering.</summary>
public sealed record FeedbackView(
    AnswerState State, string Answer, bool AnswerIsCode, bool ShowAnswer, string Explanation, bool CanOverride,
    SqlTable? SqlResult = null, string? PlanText = null, bool IsExercise = false, CodeLanguage AnswerSyntax = CodeLanguage.CSharp,
    string? Note = null, bool PassedAfterRetries = false)
{
    public bool HasPlan => PlanText is not null;
    public bool IsCorrect => State == AnswerState.Correct;
    public bool IsIncorrect => State == AnswerState.Incorrect;
    public bool NeedsSelfGrade => State == AnswerState.NeedsSelfGrade;
    public string Title => State switch
    {
        AnswerState.Correct when IsExercise => "✔  All tests pass",
        AnswerState.Correct => "✔  Correct",
        AnswerState.Incorrect when IsExercise => "Here's a solution",
        AnswerState.Incorrect => "✘  Not quite",
        _ => "How did you do?",
    };
    public string AnswerLabel => IsExercise ? (IsCorrect ? "ONE WAY TO WRITE IT" : "REFERENCE SOLUTION") : "ANSWER";
    public bool HasNote => Note is not null;
    public bool ShowAnswerAsText => ShowAnswer && !AnswerIsCode;
    public bool ShowAnswerAsCode => ShowAnswer && AnswerIsCode;
    public bool ShowNext => !NeedsSelfGrade;
    public bool ShowGuessed => IsCorrect && !IsExercise;
    public bool ShowOverride => IsIncorrect && CanOverride;
    public bool HasSqlResult => SqlResult is not null;
    public string SqlResultCaption => SqlResult is null ? "" : SqlResult.Rows.Count == 1 ? "Result · 1 row" : $"Result · {SqlResult.Rows.Count} rows";
}

/// <summary>A setup table shown with a SQL question; <see cref="RowMatters"/> is filled in once answered.</summary>
public sealed record SqlTableItem(SqlTable Table, IReadOnlyList<bool>? RowMatters);

public enum TestState { Pending, Passed, Failed }

/// <summary>One test of a C# exercise, before (Pending) or after running.</summary>
public sealed record TestRow(int Number, string Call, string Expected, string? Actual, string? Error, TestState State)
{
    public string Icon => State switch { TestState.Passed => "✔", TestState.Failed => "✘", _ => "•" };
    public bool IsPassed => State == TestState.Passed;
    public bool IsFailed => State == TestState.Failed;
    public string Detail => State switch
    {
        TestState.Pending => $"→ {Expected}",
        TestState.Passed => $"→ {Actual}",
        _ when Actual is not null => $"expected {Expected}, got {Actual}",
        _ when Error is not null => $"expected {Expected}: {Error}",
        _ => $"expected {Expected}",
    };
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
    private readonly QuizOptions _options;
    private StudySession? _session;

    private QuizMode _mode;
    private QuestionView? _question;
    private FeedbackView? _feedback;
    private string _typedAnswer = "";
    private bool _isFlagging;
    private string _flagNote = "";
    private bool _flagged;
    private string _summaryText = "";
    private string _nextReviewText = "";
    private string _sessionDescription = "";
    private SessionPickerViewModel? _picker;

    public QuizViewModel(QuizService service, QuizOptions? options = null)
    {
        _service = service;
        _options = options ?? new QuizOptions();

        OpenSettingsCommand = new RelayCommand(() => _options.OpenSettings?.Invoke());
        OpenStatsCommand = new RelayCommand(() => _options.OpenStats?.Invoke());
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
        ShowPickerCommand = new RelayCommand(ShowPicker);
        RunCommand = new AsyncRelayCommand(RunExerciseAsync);
        GiveUpCommand = new RelayCommand(GiveUp);
        ExplainCommand = new AsyncRelayCommand(ExplainAsync);

        if (_options.StartWithPicker) ShowPicker();
        else StartSession(_options.Filter);
    }

    public event EventHandler? CloseRequested;

    public ObservableCollection<OptionViewModel> Options { get; } = [];

    /// <summary>The setup tables of a SQL question (empty otherwise). Row highlights are added after answering.</summary>
    public ObservableCollection<SqlTableItem> SqlTables { get; } = [];

    private SqlRun? _sqlRun;

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
    public RelayCommand OpenStatsCommand { get; }
    public RelayCommand ShowPickerCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand GiveUpCommand { get; }
    public AsyncRelayCommand ExplainCommand { get; }

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
            OnPropertyChanged(nameof(ShowPickerPanel));
            OnPropertyChanged(nameof(IsCodingNow));
            OnPropertyChanged(nameof(CanExplain));
        }
    }

    public bool ShowQuestion => Mode is QuizMode.Question or QuizMode.Feedback;
    public bool IsAnswering => Mode == QuizMode.Question;
    public bool ShowFeedback => Mode == QuizMode.Feedback;
    public bool ShowFinished => Mode == QuizMode.Finished;
    public bool ShowEmpty => Mode == QuizMode.Empty;
    public bool IsDone => Mode is QuizMode.Finished or QuizMode.Empty;
    public bool ShowPickerPanel => Mode == QuizMode.Picker;

    public SessionPickerViewModel? Picker
    {
        get => _picker;
        private set => SetProperty(ref _picker, value);
    }

    public string SessionDescription
    {
        get => _sessionDescription;
        private set => SetProperty(ref _sessionDescription, value);
    }

    public QuestionView? Question
    {
        get => _question;
        private set
        {
            if (SetProperty(ref _question, value)) OnPropertyChanged(nameof(IsCodingNow));
        }
    }

    public FeedbackView? Feedback
    {
        get => _feedback;
        private set
        {
            if (SetProperty(ref _feedback, value)) OnPropertyChanged(nameof(CanExplain));
        }
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

    // ------------------------------------------------------------------ choosing what to study

    public void ShowPicker()
    {
        if (Mode is QuizMode.Question or QuizMode.Feedback) return; // finish or close the current session first
        _session?.End();
        Picker = new SessionPickerViewModel(_service, _options.Filter, filter =>
        {
            _options.FilterChosen?.Invoke(filter);
            StartSession(filter);
        });
        Mode = QuizMode.Picker;
    }

    public void StartPickedSession() => Picker?.StartCommand.Execute(null);

    private void StartSession(SessionFilter filter)
    {
        var plan = _service.PlanSession(filter);
        _session = new StudySession(_service, plan, filter: filter);
        SessionDescription = plan.IsEmpty ? ""
            : $"{plan.DueReviews} to review · {plan.NewCards} new" + (filter.IsEverything ? "" : $" · {filter.Describe()}");
        Advance();
    }

    // ------------------------------------------------------------------ actions

    public void Choose(int index)
    {
        if (Mode != QuizMode.Question || _session?.Choices is not { } choices || index < 0 || index >= choices.Options.Count) return;

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
        ShowResult(_session!.SubmitTyped(TypedAnswer.TrimEnd()), showAnswer: true);
    }

    /// <summary>Accept the default outcome: right if correct (or "struggled" for an exercise passed after failed runs), wrong if not.</summary>
    public void Next()
    {
        if (Mode != QuizMode.Feedback || Feedback is null || Feedback.NeedsSelfGrade) return;
        Complete(!Feedback.IsCorrect ? AnswerOutcome.Wrong : Feedback.PassedAfterRetries ? AnswerOutcome.Guessed : AnswerOutcome.Right);
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
        if (!IsFlagging || _session?.Current is not { } item) return;
        _service.Flag(item.Question.Id, FlagNote);
        IsFlagging = false;
        Flagged = true;
    }

    public void CancelFlag() => IsFlagging = false;

    /// <summary>Called when the window closes: an answered-but-unconfirmed question still counts; exercise code is kept as a draft.</summary>
    public void CompletePendingOnClose()
    {
        CancelExplain();
        if (Mode == QuizMode.Question && _session?.Current is { Question.IsExercise: true } item)
            SaveDraft(item);
        if (Mode == QuizMode.Feedback && Feedback is { NeedsSelfGrade: false }) Next();
        _session?.End();
    }

    // ------------------------------------------------------------------ coding exercises

    private string _exerciseCode = "";
    private string _exerciseStarter = "";
    private bool _isRunning;
    private string _runSummary = "";
    private bool _runFailed;
    private string _runOutput = "";
    private int _failedRuns;
    private ExerciseResult? _lastRun;
    private SqlTable? _sqlUserResult;
    private SqlTable? _sqlExpectedResult;

    public ObservableCollection<TestRow> TestRows { get; } = [];
    public ObservableCollection<string> CompileErrors { get; } = [];

    /// <summary>Answering a coding exercise right now: the editor has the keyboard.</summary>
    public bool IsCodingNow => Mode == QuizMode.Question && Question is { IsExercise: true };

    public string ExerciseCode
    {
        get => _exerciseCode;
        set => SetProperty(ref _exerciseCode, value);
    }

    public CodeLanguage ExerciseSyntax => _session?.Current?.Question.Language == ExerciseLanguage.Sql ? CodeLanguage.Sql : CodeLanguage.CSharp;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(RunButtonText));
        }
    }

    public string RunButtonText => IsRunning ? "Running…" : ExerciseSyntax == CodeLanguage.Sql ? "Run query (Ctrl+Enter)" : "Run tests (Ctrl+Enter)";

    public string RunSummary
    {
        get => _runSummary;
        private set
        {
            if (SetProperty(ref _runSummary, value)) OnPropertyChanged(nameof(HasRunSummary));
        }
    }

    public bool HasRunSummary => RunSummary.Length > 0;

    public bool RunFailed
    {
        get => _runFailed;
        private set => SetProperty(ref _runFailed, value);
    }

    public string RunOutput
    {
        get => _runOutput;
        private set
        {
            if (SetProperty(ref _runOutput, value)) OnPropertyChanged(nameof(HasRunOutput));
        }
    }

    public bool HasRunOutput => RunOutput.Length > 0;
    public bool HasTests => TestRows.Count > 0;
    public bool HasCompileErrors => CompileErrors.Count > 0;

    public SqlTable? SqlUserResult
    {
        get => _sqlUserResult;
        private set
        {
            if (SetProperty(ref _sqlUserResult, value)) OnPropertyChanged(nameof(HasSqlUserResult));
        }
    }

    public SqlTable? SqlExpectedResult
    {
        get => _sqlExpectedResult;
        private set
        {
            if (SetProperty(ref _sqlExpectedResult, value)) OnPropertyChanged(nameof(HasSqlExpectedResult));
        }
    }

    public bool HasSqlUserResult => SqlUserResult is not null;
    public bool HasSqlExpectedResult => SqlExpectedResult is not null;

    public async Task RunExerciseAsync()
    {
        if (Mode != QuizMode.Question || _session?.Current is not { Question.IsExercise: true } item || IsRunning) return;
        if (string.IsNullOrWhiteSpace(ExerciseCode) || ExerciseCode.Trim() == _exerciseStarter.Trim() && item.Question.Language == ExerciseLanguage.Sql)
        {
            RunFailed = true;
            RunSummary = item.Question.Language == ExerciseLanguage.Sql ? "Write a query first." : "Write some code first.";
            return;
        }

        IsRunning = true;
        RunSummary = "Running…";
        RunFailed = false;
        var code = ExerciseCode;
        try
        {
            var result = await ExerciseRunner.RunAsync(item.Question, code);
            if (_session.Current != item || Mode != QuizMode.Question) return; // moved on while it ran
            _lastRun = result;
            ShowRun(item.Question, result);
            _service.LogEvent(StudyEvent.ExerciseRun, result.Status == ExerciseStatus.CompileError
                ? $"{item.Question.Id}: compile error"
                : $"{item.Question.Id}: {result.PassedCount}/{result.TotalCount} passed ({result.Status.ToString().ToLowerInvariant()})");

            if (result.AllPassed)
            {
                _options.SaveDraft(item.Question.Id, null);
                ShowResult(_session.SubmitExercise(code, passed: true), showAnswer: true);
            }
            else if (result.Status != ExerciseStatus.CompileError)
            {
                _failedRuns++; // typos that don't compile aren't held against you
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Running exercise {item.Question.Id} failed", ex);
            RunFailed = true;
            RunSummary = $"Couldn't run the code: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void GiveUp()
    {
        if (Mode != QuizMode.Question || _session?.Current is not { Question.IsExercise: true } item || IsRunning) return;
        _options.SaveDraft(item.Question.Id, null);
        ShowResult(_session.SubmitExercise(ExerciseCode, passed: false), showAnswer: true);
    }

    private void ShowRun(Question q, ExerciseResult result)
    {
        RunFailed = !result.AllPassed;
        RunSummary = result.Summary;
        RunOutput = result.Output.Length > 4000 ? result.Output[..4000] + "\n…" : result.Output;

        CompileErrors.Clear();
        foreach (var e in result.CompileErrors) CompileErrors.Add(e.ToString());
        OnPropertyChanged(nameof(HasCompileErrors));

        if (q.Language == ExerciseLanguage.Sql)
        {
            SqlUserResult = result.Sql?.Actual is { } actual ? actual with { Name = "Your result" } : null;
            // The expected rows only after a real attempt (a query that ran), never for an empty or broken one.
            SqlExpectedResult = result.Sql is { Passed: false, Actual: not null } sql ? sql.Expected with { Name = "Expected" } : null;
        }
        else if (result.Status != ExerciseStatus.CompileError)
        {
            TestRows.Clear();
            foreach (var t in result.Tests)
            {
                var state = t.Passed ? TestState.Passed : t.Ran || t.Error is not null and not "didn't run" ? TestState.Failed : TestState.Pending;
                TestRows.Add(new TestRow(t.Index + 1, t.Call, t.Expected, t.Actual, t.Error, state));
            }
        }
    }

    private void ResetExercise(Question q)
    {
        _exerciseStarter = q.Starter ?? "";
        ExerciseCode = _options.LoadDraft(q.Id) ?? _exerciseStarter;
        _failedRuns = 0;
        _lastRun = null;
        RunSummary = "";
        RunFailed = false;
        RunOutput = "";
        SqlUserResult = null;
        SqlExpectedResult = null;
        CompileErrors.Clear();
        TestRows.Clear();
        foreach (var (t, i) in q.Tests.Select((t, i) => (t, i)))
            TestRows.Add(new TestRow(i + 1, t.Call, t.Expect, null, null, TestState.Pending));
        OnPropertyChanged(nameof(HasTests));
        OnPropertyChanged(nameof(HasCompileErrors));
        OnPropertyChanged(nameof(ExerciseSyntax));
        OnPropertyChanged(nameof(RunButtonText));
    }

    private void SaveDraft(SessionItem item)
    {
        var code = ExerciseCode;
        var changed = !string.IsNullOrWhiteSpace(code) && code.Trim() != _exerciseStarter.Trim();
        _options.SaveDraft(item.Question.Id, changed ? code : null);
    }

    // ------------------------------------------------------------------ explain my mistake (Claude)

    private string _explainText = "";
    private string _explainError = "";
    private bool _isExplaining;
    private bool _explainNeedsKey;
    private CancellationTokenSource? _explainCts;

    public string ExplainText
    {
        get => _explainText;
        private set
        {
            if (!SetProperty(ref _explainText, value)) return;
            OnPropertyChanged(nameof(HasExplainText));
            OnPropertyChanged(nameof(ShowExplainPanel));
            OnPropertyChanged(nameof(CanExplain));
        }
    }

    public string ExplainError
    {
        get => _explainError;
        private set
        {
            if (!SetProperty(ref _explainError, value)) return;
            OnPropertyChanged(nameof(HasExplainError));
            OnPropertyChanged(nameof(ShowExplainPanel));
        }
    }

    public bool HasExplainError => ExplainError.Length > 0;
    public bool HasExplainText => ExplainText.Length > 0;

    public bool IsExplaining
    {
        get => _isExplaining;
        private set
        {
            if (!SetProperty(ref _isExplaining, value)) return;
            OnPropertyChanged(nameof(CanExplain));
            OnPropertyChanged(nameof(ShowExplainPanel));
        }
    }

    public bool ExplainNeedsKey
    {
        get => _explainNeedsKey;
        private set
        {
            if (!SetProperty(ref _explainNeedsKey, value)) return;
            OnPropertyChanged(nameof(ShowExplainPanel));
            OnPropertyChanged(nameof(CanExplain));
        }
    }

    /// <summary>The "Ask Claude why" button: after a wrong answer (or a self-graded one), until asked.</summary>
    public bool CanExplain => Mode == QuizMode.Feedback && Feedback is { IsCorrect: false } && !IsExplaining && ExplainText.Length == 0 && !ExplainNeedsKey;

    public bool ShowExplainPanel => IsExplaining || ExplainText.Length > 0 || HasExplainError || ExplainNeedsKey;

    public async Task ExplainAsync()
    {
        if (!CanExplain || _session?.Current is not { } item || _session.Pending is not { } pending) return;

        var key = _options.GetApiKey();
        if (key is null)
        {
            ExplainNeedsKey = true;
            return;
        }

        CancelExplain();
        var cts = _explainCts = new CancellationTokenSource();
        ExplainError = "";
        ExplainText = "";
        IsExplaining = true;
        _service.LogEvent(StudyEvent.ExplanationRequested, item.Question.Id);

        var request = new MistakeRequest(item.Question, pending.Response,
            WasMultipleChoice: item.Stage == CardStage.MultipleChoice && !item.Question.IsExercise,
            RunReport: item.Question.IsExercise ? _lastRun?.Describe() ?? "They gave up before running the code." : null);
        try
        {
            await foreach (var chunk in _options.CreateExplainer(key).ExplainAsync(request, cts.Token))
            {
                if (cts.IsCancellationRequested) return;
                ExplainText += chunk;
            }
        }
        catch (OperationCanceledException)
        {
            // Moved on to the next question.
        }
        catch (ExplainException ex)
        {
            if (cts.IsCancellationRequested) return;
            ExplainError = ex.Message;
            Log.Error($"Explain failed for {item.Question.Id}", ex.InnerException ?? ex);
            _service.LogEvent(StudyEvent.ExplanationRequested, $"{item.Question.Id} failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_explainCts, cts)) IsExplaining = false;
        }
    }

    private void CancelExplain()
    {
        _explainCts?.Cancel();
        _explainCts = null;
    }

    private void ResetExplain()
    {
        CancelExplain();
        IsExplaining = false;
        ExplainText = "";
        ExplainError = "";
        ExplainNeedsKey = false;
    }

    // ------------------------------------------------------------------ flow

    private void ShowResult(PendingAnswer pending, bool showAnswer)
    {
        var item = _session!.Current!;
        var q = item.Question;
        var answerIsCode = q.Type != QuestionType.Concept || q.Answer.Contains('\n');
        // Typed answers that weren't right show the real answer. MC highlights it in the options instead.
        var revealAnswer = (showAnswer && pending.State != AnswerState.Correct) || pending.State == AnswerState.NeedsSelfGrade;

        if (q.IsExercise)
        {
            var retried = pending.State == AnswerState.Correct && _failedRuns > 0;
            var note = pending.State != AnswerState.Correct ? null
                : retried ? $"Passed after {_failedRuns} failed run{(_failedRuns == 1 ? "" : "s")}, so it comes back a little sooner. Press E if it was really easy."
                : "Passed on the first run.";
            Feedback = new FeedbackView(pending.State, q.Answer, AnswerIsCode: true, ShowAnswer: true, q.Explanation, CanOverride: false,
                IsExercise: true, AnswerSyntax: q.Language == ExerciseLanguage.Sql ? CodeLanguage.Sql : CodeLanguage.CSharp,
                Note: note, PassedAfterRetries: retried);
            Mode = QuizMode.Feedback;
            return;
        }

        // SQL: the result grid shows the rows, so only a row count needs repeating as text.
        // Plan questions show the query plan instead of the result and row highlights.
        SqlTable? sqlResult = null;
        string? planText = null;
        if (_sqlRun is not null)
        {
            answerIsCode = false;
            if (q.EffectiveExpect == ResultExpectation.TableAccess)
            {
                planText = string.Join('\n', _sqlRun.Plan.Select(s => new string(' ', s.Depth * 3) + s.Detail));
            }
            else
            {
                if (q.EffectiveExpect == ResultExpectation.Rows) revealAnswer = false;
                sqlResult = _sqlRun.Result;
                var tables = _sqlRun.Tables.Select((t, i) => new SqlTableItem(t, _sqlRun.RowMatters?[i])).ToList();
                SqlTables.Clear();
                foreach (var t in tables) SqlTables.Add(t);
            }
        }

        Feedback = new FeedbackView(pending.State, q.Answer, answerIsCode, revealAnswer, q.Explanation, pending.CanOverride, sqlResult, planText);
        Mode = QuizMode.Feedback;
    }

    private void Complete(AnswerOutcome outcome)
    {
        _session!.Complete(outcome);
        Advance();
    }

    private void Advance()
    {
        IsFlagging = false;
        Flagged = false;
        Feedback = null;
        TypedAnswer = "";
        Options.Clear();
        SqlTables.Clear();
        _sqlRun = null;
        ResetExplain();

        if (_session is null || !_session.MoveNext())
        {
            Finish();
            return;
        }

        var item = _session.Current!;
        var q = item.Question;

        if (q.IsExercise)
        {
            ShowExercise(item);
            return;
        }

        var isMc = item.Stage == CardStage.MultipleChoice;
        var isSql = q.Type == QuestionType.PredictResult;
        var isRowCount = isSql && q.EffectiveExpect == ResultExpectation.RowCount;
        var isPlan = isSql && q.EffectiveExpect == ResultExpectation.TableAccess;
        var isSingleValue = isRowCount || isPlan;
        var multiLine = (q.Type != QuestionType.Concept && !isSingleValue) || q.EffectiveRecall == RecallGrading.Self;
        var hint = q.Type switch
        {
            QuestionType.PredictOutput => "Type the exact output. Ctrl+Enter to submit.",
            QuestionType.PredictResult when isRowCount => "Type the number of rows and press Enter.",
            QuestionType.PredictResult when isPlan => "Type how the table is read (e.g. Full table scan, Index seek) and press Enter.",
            QuestionType.PredictResult => "Type the result: one row per line, values separated by commas, NULL for nulls. Ctrl+Enter to submit.",
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
            CodeSyntax: q.Type == QuestionType.PredictResult ? CodeLanguage.Sql : CodeLanguage.CSharp,
            IsMultipleChoice: isMc,
            IsMultiLineAnswer: multiLine,
            TypedHint: hint);

        if (_session.Choices is { } choices)
        {
            for (var i = 0; i < choices.Options.Count; i++)
            {
                var text = choices.Options[i];
                var isCode = q.Type is QuestionType.PickTheCode || (isSql && !isSingleValue) || text.Contains('\n');
                Options.Add(new OptionViewModel(i, text, isCode));
            }
        }

        if (q.Type == QuestionType.PredictResult)
        {
            try
            {
                _sqlRun = SqlSandbox.Run(q, analyseRows: true);
                foreach (var t in _sqlRun.Tables) SqlTables.Add(new SqlTableItem(t, null));
            }
            catch (SqlRunException ex)
            {
                // Content is verified by the test suite, so this means a bad import; show the question without tables.
                Log.Error($"SQL for {q.Id} failed", ex);
            }
        }

        Mode = QuizMode.Question;
    }

    private void ShowExercise(SessionItem item)
    {
        var q = item.Question;
        var isSql = q.Language == ExerciseLanguage.Sql;
        Question = new QuestionView(
            PositionText: $"{_session!.Position} / {_session.Total}",
            Progress: (double)(_session.Position - 1) / _session.Total,
            Topic: q.Topic,
            DifficultyText: $"Difficulty {q.Difficulty}",
            StageText: isSql ? "Write the query" : "Write the code",
            IsNew: item.IsNew,
            Prompt: q.Prompt,
            Code: isSql ? null : q.Helpers,
            CodeSyntax: CodeLanguage.CSharp,
            IsMultipleChoice: false,
            IsMultiLineAnswer: true,
            TypedHint: "",
            IsExercise: true,
            CodeCaption: "PROVIDED (you can use these)");
        ResetExercise(q);

        if (isSql)
        {
            try
            {
                foreach (var t in SqlSandbox.Run(q.Setup ?? "", q.Answer).Tables) SqlTables.Add(new SqlTableItem(t, null));
            }
            catch (SqlRunException ex)
            {
                Log.Error($"SQL setup for {q.Id} failed", ex);
            }
        }
        else
        {
            _ = ExerciseRunner.WarmUpAsync(); // so the first run isn't slowed by the compiler starting up
        }

        Mode = QuizMode.Question;
    }

    private void Finish()
    {
        var summary = _service.GetDueSummary();
        var now = _service.Now;
        var filtered = _session is { Filter.IsEverything: false };
        NextReviewText = summary.Total > 0
            ? filtered ? $"{summary.Total} more ready in other kinds of question." : $"{summary.Total} more ready now. Open Quizzler again to keep going."
            : summary.NextDueAt is { } next
                ? $"Next review {TimeText.Until(next - now)}. Quizzler will remind you."
                : "Add more questions to keep learning.";

        if (_session is null || _session.Total == 0)
        {
            SummaryText = filtered ? "Nothing ready for that choice" : "All caught up!";
            Mode = QuizMode.Empty;
            return;
        }

        var pct = _session.Answered == 0 ? 0 : 100 * _session.CorrectCount / _session.Answered;
        SummaryText = $"{_session.CorrectCount} of {_session.Answered} correct ({pct}%)";
        Mode = QuizMode.Finished;
    }
}
