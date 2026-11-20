using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Quizzler.App.Services;

public enum CodeLanguage { CSharp, Sql, PlainText }

/// <summary>
/// Syntax colours for code blocks. AvalonEdit's built-in schemes are designed for white backgrounds,
/// so dark mode uses separate copies recoloured along the lines of VS Code's "Dark+".
/// </summary>
internal static class CodeHighlighting
{
    private static readonly Lazy<IHighlightingDefinition> CSharpLight = new(() => HighlightingManager.Instance.GetDefinitionByExtension(".cs"));
    private static readonly Lazy<IHighlightingDefinition> CSharpDark = new(() => CreateDark("CSharp-Mode.xshd"));
    private static readonly Lazy<IHighlightingDefinition> SqlLight = new(() => HighlightingManager.Instance.GetDefinitionByExtension(".sql"));
    private static readonly Lazy<IHighlightingDefinition> SqlDark = new(() => CreateDark("TSQL-Mode.xshd"));

    public static IHighlightingDefinition? For(CodeLanguage language) => (language, ThemeService.IsDark) switch
    {
        (CodeLanguage.CSharp, false) => CSharpLight.Value,
        (CodeLanguage.CSharp, true) => CSharpDark.Value,
        (CodeLanguage.Sql, false) => SqlLight.Value,
        (CodeLanguage.Sql, true) => SqlDark.Value,
        _ => null,
    };

    private static IHighlightingDefinition CreateDark(string resource)
    {
        var assembly = typeof(HighlightingManager).Assembly;
        using var stream = assembly.GetManifestResourceStream("ICSharpCode.AvalonEdit.Highlighting.Resources." + resource)!;
        using var reader = XmlReader.Create(stream);
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);

        foreach (var color in definition.NamedHighlightingColors)
            color.Foreground = new SimpleHighlightingBrush(DarkColorFor(color.Name));
        return definition;
    }

    // Matched by name fragments so the same palette works for both the C# and T-SQL schemes.
    private static Color DarkColorFor(string name) => name switch
    {
        _ when name.Contains("Comment", StringComparison.OrdinalIgnoreCase) => Rgb(0x6A, 0x99, 0x55),
        _ when name.Contains("String", StringComparison.OrdinalIgnoreCase) || name == "Char" => Rgb(0xCE, 0x91, 0x78),
        _ when name.Contains("Number", StringComparison.OrdinalIgnoreCase) || name.Contains("Digit", StringComparison.OrdinalIgnoreCase) => Rgb(0xB5, 0xCE, 0xA8),
        "MethodCall" => Rgb(0xDC, 0xDC, 0xAA),
        _ when name.Contains("Function", StringComparison.OrdinalIgnoreCase) => Rgb(0xDC, 0xDC, 0xAA),
        "Preprocessor" => Rgb(0x9B, 0x9B, 0x9B),
        "Punctuation" or "Operator" or "Operators" => Rgb(0xD4, 0xD4, 0xD4),
        "GotoKeywords" or "ExceptionKeywords" or "CheckedKeyword" or "UnsafeKeywords" => Rgb(0xC5, 0x86, 0xC0),
        _ => Rgb(0x56, 0x9C, 0xD6), // keywords, type keywords, modifiers, true/false, null...
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
