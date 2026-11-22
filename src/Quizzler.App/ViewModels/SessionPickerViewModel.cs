using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;

namespace Quizzler.App.ViewModels;

public sealed class KindOption(QuestionKind kind, Action changed) : ObservableObject
{
    public QuestionKind Kind { get; } = kind;
    public string Label { get; } = QuestionKinds.Label(kind);
    public bool IsExercise { get; } = QuestionKinds.IsExercise(kind);

    private bool _isChecked = true;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value)) changed();
        }
    }

    private int _count;
    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(CountText));
        }
    }

    public string CountText => Count == 0 ? "none ready" : $"{Count} ready";
}

public sealed record StyleOption(AnswerStyle Style, string Label, string Hint);

/// <summary>"Choose what to study": answer style + question kinds, with live counts of what each choice would give.</summary>
public sealed class SessionPickerViewModel : ObservableObject
{
    private readonly QuizService _service;
    private StyleOption _style;
    private int _available;
    private bool _refreshing;

    public SessionPickerViewModel(QuizService service, SessionFilter initial, Action<SessionFilter> start)
    {
        _service = service;
        Kinds = new ObservableCollection<KindOption>(QuestionKinds.All.Select(k => new KindOption(k, Refresh)));
        _style = Styles.First(s => s.Style == initial.Style);
        Apply(initial);

        StartCommand = new RelayCommand(() => { if (CanStart) start(CurrentFilter); });
        SelectStyleCommand = new RelayCommand<StyleOption>(s => { if (s is not null) Style = s; });
        EverythingCommand = new RelayCommand(() => Apply(SessionFilter.Everything));
        MultipleChoiceCommand = new RelayCommand(() => Apply(new SessionFilter { Style = AnswerStyle.MultipleChoiceOnly }));
        TypedCommand = new RelayCommand(() => Apply(new SessionFilter { Style = AnswerStyle.TypedOnly }));
        CodingCommand = new RelayCommand(() => Apply(new SessionFilter { Kinds = Set(QuestionKinds.Exercises) }));
    }

    public IReadOnlyList<StyleOption> Styles { get; } =
    [
        new(AnswerStyle.Mixed, "As scheduled", "New and shaky questions as multiple choice, learned ones typed."),
        new(AnswerStyle.MultipleChoiceOnly, "Multiple choice only", "New questions, and ones at the multiple-choice stage."),
        new(AnswerStyle.TypedOnly, "Typed only", "Questions you've learned well enough to type. No new ones."),
    ];

    public ObservableCollection<KindOption> Kinds { get; }

    public RelayCommand StartCommand { get; }
    public RelayCommand<StyleOption> SelectStyleCommand { get; }
    public RelayCommand EverythingCommand { get; }
    public RelayCommand MultipleChoiceCommand { get; }
    public RelayCommand TypedCommand { get; }
    public RelayCommand CodingCommand { get; }

    public StyleOption Style
    {
        get => _style;
        set
        {
            if (SetProperty(ref _style, value)) Refresh();
        }
    }

    public int Available
    {
        get => _available;
        private set
        {
            if (!SetProperty(ref _available, value)) return;
            OnPropertyChanged(nameof(AvailableText));
            OnPropertyChanged(nameof(CanStart));
        }
    }

    public bool CanStart => Available > 0;

    public string AvailableText => Available switch
    {
        0 when Kinds.All(k => !k.IsChecked) => "Pick at least one question type.",
        0 => "Nothing is ready for this choice right now.",
        1 => "1 question ready",
        _ => $"{Available} questions ready",
    };

    public SessionFilter CurrentFilter
    {
        get
        {
            var chosen = Kinds.Where(k => k.IsChecked).Select(k => k.Kind).ToHashSet();
            // Nothing ticked must not mean "everything" (which is what an empty set means to the planner).
            if (chosen.Count == 0) chosen.Add((QuestionKind)(-1));
            return new SessionFilter { Style = Style.Style, Kinds = chosen.Count == QuestionKinds.All.Count ? null : chosen };
        }
    }

    public void Apply(SessionFilter filter)
    {
        _refreshing = true;
        foreach (var k in Kinds) k.IsChecked = filter.IncludesKind(k.Kind);
        Style = Styles.First(s => s.Style == filter.Style);
        _refreshing = false;
        Refresh();
    }

    public void Refresh()
    {
        if (_refreshing) return;
        var style = Style.Style;
        var filters = Kinds.Select(k => new SessionFilter { Style = style, Kinds = new HashSet<QuestionKind> { k.Kind } })
            .Append(CurrentFilter)
            .ToList();
        var plans = _service.PlanSessions(filters);
        for (var i = 0; i < Kinds.Count; i++) Kinds[i].Count = plans[i].Items.Count;
        Available = plans[^1].Items.Count;
        OnPropertyChanged(nameof(AvailableText));
    }

    private static HashSet<QuestionKind> Set(IEnumerable<QuestionKind> kinds) => kinds.ToHashSet();
}
