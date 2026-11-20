using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using Quizzler.App.Services;

namespace Quizzler.App.Views;

/// <summary>
/// Read-only, C#-highlighted code block that sizes to its content, lets the page scroll past it,
/// and recolours itself when the app switches between light and dark.
/// </summary>
public sealed class CodeViewer : TextEditor
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(CodeViewer),
        new PropertyMetadata("", (d, e) => ((CodeViewer)d).Text = (string?)e.NewValue ?? ""));

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public static readonly DependencyProperty SyntaxProperty = DependencyProperty.Register(
        nameof(Syntax), typeof(CodeLanguage), typeof(CodeViewer),
        new PropertyMetadata(CodeLanguage.CSharp, (d, _) => ((CodeViewer)d).OnThemeChanged(null, EventArgs.Empty)));

    public CodeLanguage Syntax
    {
        get => (CodeLanguage)GetValue(SyntaxProperty);
        set => SetValue(SyntaxProperty, value);
    }

    public CodeViewer()
    {
        IsReadOnly = true;
        SyntaxHighlighting = CodeHighlighting.For(CodeLanguage.CSharp);
        FontFamily = (FontFamily)Application.Current.FindResource("CodeFont");
        FontSize = 14;
        ShowLineNumbers = false;
        WordWrap = false;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "CodeForegroundBrush");
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.HighlightCurrentLine = false;
        TextArea.Caret.CaretBrush = Brushes.Transparent;

        // Static event: subscribe only while on screen so closed windows can be collected.
        Loaded += (_, _) => { ThemeService.Changed += OnThemeChanged; OnThemeChanged(null, EventArgs.Empty); };
        Unloaded += (_, _) => ThemeService.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var wanted = CodeHighlighting.For(Syntax);
        if (!ReferenceEquals(SyntaxHighlighting, wanted))
            SyntaxHighlighting = wanted;
    }

    /// <summary>The editor would swallow the mouse wheel; pass it to the page's ScrollViewer instead.</summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (e.Handled || Parent is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = this,
        });
    }
}
