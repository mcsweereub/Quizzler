using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using Quizzler.App.Services;

namespace Quizzler.App.Views;

/// <summary>
/// Editable code box for coding exercises: syntax highlighting (C# or SQL), line numbers (compile errors refer to
/// them), spaces for tabs, auto-indent. <see cref="Code"/> binds two-way.
/// </summary>
public sealed class CodeEditor : TextEditor
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(CodeEditor),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnCodeChanged));

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public static readonly DependencyProperty SyntaxProperty = DependencyProperty.Register(
        nameof(Syntax), typeof(CodeLanguage), typeof(CodeEditor),
        new PropertyMetadata(CodeLanguage.CSharp, (d, _) => ((CodeEditor)d).OnThemeChanged(null, EventArgs.Empty)));

    public CodeLanguage Syntax
    {
        get => (CodeLanguage)GetValue(SyntaxProperty);
        set => SetValue(SyntaxProperty, value);
    }

    private bool _updating;

    public CodeEditor()
    {
        SyntaxHighlighting = CodeHighlighting.For(CodeLanguage.CSharp);
        FontFamily = (FontFamily)Application.Current.FindResource("CodeFont");
        FontSize = 14;
        ShowLineNumbers = true;
        WordWrap = false;
        Padding = new Thickness(4, 6, 4, 6);
        SetResourceReference(BackgroundProperty, "CodeBackgroundBrush");
        SetResourceReference(ForegroundProperty, "CodeForegroundBrush");
        SetResourceReference(LineNumbersForegroundProperty, "MutedTextBrush");
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Options.ConvertTabsToSpaces = true;
        Options.IndentationSize = 4;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.HighlightCurrentLine = true;
        TextArea.SetResourceReference(TextBlock.ForegroundProperty, "CodeForegroundBrush");
        TextArea.Caret.CaretBrush = (Brush)Application.Current.FindResource("CodeForegroundBrush");

        TextChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            Code = Text;
            _updating = false;
        };

        Loaded += (_, _) => { ThemeService.Changed += OnThemeChanged; OnThemeChanged(null, EventArgs.Empty); };
        Unloaded += (_, _) => ThemeService.Changed -= OnThemeChanged;
    }

    private static void OnCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        if (editor._updating) return;
        editor._updating = true;
        editor.Text = (string?)e.NewValue ?? "";
        editor._updating = false;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var wanted = CodeHighlighting.For(Syntax);
        if (!ReferenceEquals(SyntaxHighlighting, wanted)) SyntaxHighlighting = wanted;
        TextArea.Caret.CaretBrush = (Brush)Application.Current.FindResource("CodeForegroundBrush");
    }
}
