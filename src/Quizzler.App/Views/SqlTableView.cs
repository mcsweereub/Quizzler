using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quizzler.Data;

namespace Quizzler.App.Views;

/// <summary>
/// Draws a small SQL table (setup data or a query result) as a grid. When <see cref="RowMatters"/> is set, rows
/// that affect the query's result are highlighted and the rest are dimmed. All colours are theme resources.
/// </summary>
public sealed class SqlTableView : Border
{
    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
        nameof(Table), typeof(SqlTable), typeof(SqlTableView), new PropertyMetadata(null, (d, _) => ((SqlTableView)d).Rebuild()));

    public static readonly DependencyProperty RowMattersProperty = DependencyProperty.Register(
        nameof(RowMatters), typeof(IReadOnlyList<bool>), typeof(SqlTableView), new PropertyMetadata(null, (d, _) => ((SqlTableView)d).Rebuild()));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(SqlTableView), new PropertyMetadata(null, (d, _) => ((SqlTableView)d).Rebuild()));

    public SqlTable? Table
    {
        get => (SqlTable?)GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    public IReadOnlyList<bool>? RowMatters
    {
        get => (IReadOnlyList<bool>?)GetValue(RowMattersProperty);
        set => SetValue(RowMattersProperty, value);
    }

    /// <summary>Text above the grid; defaults to the table's name.</summary>
    public string? Caption
    {
        get => (string?)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    private void Rebuild()
    {
        if (Table is not { } table)
        {
            Child = null;
            return;
        }

        var codeFont = (FontFamily)Application.Current.FindResource("CodeFont");
        var grid = new Grid();
        foreach (var _ in table.Columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var rowCount = Math.Max(table.Rows.Count, 1) + 1;
        for (var r = 0; r < rowCount; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var c = 0; c < table.Columns.Count; c++)
        {
            var header = Cell(table.Columns[c], c, 0, isLastRow: false);
            header.SetResourceReference(BackgroundProperty, "ChipBackgroundBrush");
            ((TextBlock)header.Child).FontWeight = FontWeights.SemiBold;
            grid.Children.Add(header);
        }

        if (table.Rows.Count == 0)
        {
            var empty = new TextBlock { Text = SqlSandbox.NoRows, Margin = new Thickness(10, 5, 10, 5), FontStyle = FontStyles.Italic };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, Math.Max(table.Columns.Count, 1));
            grid.Children.Add(empty);
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var matters = RowMatters is { } flags && r < flags.Count ? flags[r] : (bool?)null;
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var value = c < table.Rows[r].Count ? table.Rows[r][c] : "";
                var cell = Cell(value, c, r + 1, isLastRow: r == table.Rows.Count - 1);
                var text = (TextBlock)cell.Child;
                text.FontFamily = codeFont;
                if (value == SqlSandbox.Null)
                {
                    text.FontStyle = FontStyles.Italic;
                    text.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                }
                if (matters == true) cell.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
                if (matters == false) cell.Opacity = 0.4;
                grid.Children.Add(cell);
            }
        }

        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = grid,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        frame.SetResourceReference(BorderBrushProperty, "BorderBrushSoft");
        frame.SetResourceReference(BackgroundProperty, "SurfaceBrush");

        var caption = new TextBlock
        {
            Text = Caption ?? table.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 5),
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var panel = new StackPanel { Children = { caption, frame } };
        foreach (var index in table.IndexList)
        {
            var line = new TextBlock
            {
                Text = index,
                FontFamily = codeFont,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            panel.Children.Add(line);
        }
        Child = panel;
    }

    private static Border Cell(string text, int column, int row, bool isLastRow)
    {
        var cell = new Border
        {
            Padding = new Thickness(10, 4, 14, 4),
            BorderThickness = new Thickness(0, 0, 0, isLastRow ? 0 : 1),
            Child = new TextBlock { Text = text, FontSize = 13.5 },
        };
        cell.SetResourceReference(BorderBrushProperty, "BorderBrushSoft");
        Grid.SetColumn(cell, column);
        Grid.SetRow(cell, row);
        return cell;
    }
}
