using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Quizzler.App.Views;

/// <summary>
/// Attached property that renders a small subset of Markdown: `code` spans and *emphasis*, e.g.
/// <c>&lt;TextBlock views:InlineText.Markdown="{Binding Prompt}" /&gt;</c>.
/// </summary>
public static partial class InlineText
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.RegisterAttached(
        "Markdown", typeof(string), typeof(InlineText), new PropertyMetadata(null, OnMarkdownChanged));

    public static string? GetMarkdown(DependencyObject o) => (string?)o.GetValue(MarkdownProperty);
    public static void SetMarkdown(DependencyObject o, string? value) => o.SetValue(MarkdownProperty, value);

    // *word* or *a few words*: the opening * must be followed by a non-space, so "a * b" stays literal.
    [GeneratedRegex(@"\*(?=\S)([^*\n]+?)(?<=\S)\*")]
    private static partial Regex Emphasis();

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();

        var parts = ((string?)e.NewValue ?? "").Split('`');
        var balanced = parts.Length % 2 == 1; // an odd count of pieces means every backtick was paired
        var codeFont = (FontFamily)Application.Current.FindResource("CodeFont");

        for (var i = 0; i < parts.Length; i++)
        {
            var isCode = i % 2 == 1 && (balanced || i < parts.Length - 1);
            if (parts[i].Length == 0) continue;
            if (isCode)
            {
                var run = new Run(parts[i]) { FontFamily = codeFont, FontSize = block.FontSize * 0.92 };
                run.SetResourceReference(TextElement.BackgroundProperty, "CodeInlineBackground"); // follows theme changes
                block.Inlines.Add(run);
            }
            else
            {
                AddProse(block, i % 2 == 1 ? "`" + parts[i] : parts[i]);
            }
        }
    }

    /// <summary>Plain text, with *emphasis* rendered in italics.</summary>
    private static void AddProse(TextBlock block, string text)
    {
        var last = 0;
        foreach (Match m in Emphasis().Matches(text))
        {
            if (m.Index > last) block.Inlines.Add(new Run(text[last..m.Index]));
            block.Inlines.Add(new Run(m.Groups[1].Value) { FontStyle = FontStyles.Italic });
            last = m.Index + m.Length;
        }
        if (last < text.Length) block.Inlines.Add(new Run(text[last..]));
    }
}
