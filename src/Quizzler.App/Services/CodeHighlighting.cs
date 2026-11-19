using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Quizzler.App.Services;

/// <summary>
/// C# syntax colours for code blocks. AvalonEdit's built-in scheme is designed for white backgrounds,
/// so dark mode uses a separate copy recoloured along the lines of VS Code's "Dark+".
/// </summary>
internal static class CodeHighlighting
{
    private static readonly Lazy<IHighlightingDefinition> LightDefinition = new(() => HighlightingManager.Instance.GetDefinition("C#"));
    private static readonly Lazy<IHighlightingDefinition> DarkDefinition = new(CreateDark);

    public static IHighlightingDefinition Current => ThemeService.IsDark ? DarkDefinition.Value : LightDefinition.Value;

    private static IHighlightingDefinition CreateDark()
    {
        var assembly = typeof(HighlightingManager).Assembly;
        using var stream = assembly.GetManifestResourceStream("ICSharpCode.AvalonEdit.Highlighting.Resources.CSharp-Mode.xshd")!;
        using var reader = XmlReader.Create(stream);
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);

        foreach (var color in definition.NamedHighlightingColors)
            color.Foreground = new SimpleHighlightingBrush(DarkColorFor(color.Name));
        return definition;
    }

    private static Color DarkColorFor(string name) => name switch
    {
        "Comment" => Rgb(0x6A, 0x99, 0x55),
        "String" or "Char" or "StringInterpolation" => Rgb(0xCE, 0x91, 0x78),
        "NumberLiteral" => Rgb(0xB5, 0xCE, 0xA8),
        "MethodCall" => Rgb(0xDC, 0xDC, 0xAA),
        "Preprocessor" => Rgb(0x9B, 0x9B, 0x9B),
        "Punctuation" => Rgb(0xD4, 0xD4, 0xD4),
        "GotoKeywords" or "ExceptionKeywords" or "CheckedKeyword" or "UnsafeKeywords" => Rgb(0xC5, 0x86, 0xC0),
        _ => Rgb(0x56, 0x9C, 0xD6), // keywords, type keywords, modifiers, true/false, null...
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
