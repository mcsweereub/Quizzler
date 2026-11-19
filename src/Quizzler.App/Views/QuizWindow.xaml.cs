using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Quizzler.App.ViewModels;
using Quizzler.Core.Study;

namespace Quizzler.App.Views;

public partial class QuizWindow : Window
{
    public QuizViewModel ViewModel { get; }

    public QuizWindow(QuizViewModel viewModel, bool activate = true)
    {
        InitializeComponent();
        ShowActivated = activate;
        ViewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) => FocusForCurrentState();
        Closing += (_, _) => viewModel.CompletePendingOnClose();
    }

    /// <summary>
    /// Shortcuts are ignored briefly after the screen changes, so keys still being typed for the previous
    /// step (e.g. the tail of an answer after Enter) can't trigger "Too easy" or skip the feedback.
    /// </summary>
    private static readonly TimeSpan ShortcutGuard = TimeSpan.FromMilliseconds(600);
    private DateTime _modeChangedAt = DateTime.UtcNow;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(QuizViewModel.Mode) or nameof(QuizViewModel.IsFlagging))) return;
        if (e.PropertyName == nameof(QuizViewModel.Mode)) _modeChangedAt = DateTime.UtcNow;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (e.PropertyName == nameof(QuizViewModel.Mode))
            {
                if (ViewModel.Mode == QuizMode.Question) PageScroll.ScrollToTop();
                if (ViewModel.Mode == QuizMode.Feedback) FeedbackPanel.BringIntoView();
            }
            FocusForCurrentState();
        });
    }

    /// <summary>Typing goes to the answer box when there is one; otherwise the window takes keyboard shortcuts.</summary>
    private void FocusForCurrentState()
    {
        if (ViewModel.IsFlagging)
            FlagBox.Focus();
        else if (ViewModel.Mode == QuizMode.Question && ViewModel.Question is { IsTyped: true })
            AnswerBox.Focus();
        else
            Root.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        var vm = ViewModel;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // Typing into the answer box is never blocked; only window shortcuts are guarded.
        var typingAnswer = vm.Mode == QuizMode.Question && vm.Question is { IsTyped: true } && e.Key != Key.Enter;
        if (!typingAnswer && !vm.IsFlagging && DateTime.UtcNow - _modeChangedAt < ShortcutGuard && e.Key != Key.Escape)
        {
            e.Handled = true;
            return;
        }

        e.Handled = vm.Mode switch
        {
            QuizMode.Question when vm.Question is { IsMultipleChoice: true } => HandleChoiceKey(e.Key),
            QuizMode.Question when e.Key == Key.Enter && (ctrl || vm.Question is { IsMultiLineAnswer: false }) => Do(vm.Submit),
            QuizMode.Question when e.Key == Key.Escape => Do(Close),

            QuizMode.Feedback when vm.IsFlagging => e.Key switch
            {
                Key.Enter => Do(vm.SaveFlag),
                Key.Escape => Do(vm.CancelFlag),
                _ => false,
            },
            QuizMode.Feedback => e.Key switch
            {
                Key.Enter => Do(vm.Next),
                Key.G => Do(() => vm.CompleteIf(AnswerState.Correct, AnswerOutcome.Guessed)),
                Key.E => Do(() => vm.CompleteIf(AnswerState.Correct, AnswerOutcome.Easy)),
                Key.O => Do(vm.Override),
                Key.Y => Do(() => vm.CompleteIf(AnswerState.NeedsSelfGrade, AnswerOutcome.Right)),
                Key.N => Do(() => vm.CompleteIf(AnswerState.NeedsSelfGrade, AnswerOutcome.Wrong)),
                Key.F => Do(vm.StartFlag),
                Key.Escape => Do(Close),
                _ => false,
            },

            QuizMode.Finished or QuizMode.Empty when e.Key is Key.Enter or Key.Escape => Do(Close),
            _ => false,
        };
    }

    private bool HandleChoiceKey(Key key)
    {
        if (key == Key.Escape) return Do(Close);
        var digit = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
            _ => 0,
        };
        if (digit < 1 || digit > ViewModel.Options.Count) return false;
        ViewModel.Choose(digit - 1);
        return true;
    }

    private static bool Do(Action action)
    {
        action();
        return true;
    }
}
