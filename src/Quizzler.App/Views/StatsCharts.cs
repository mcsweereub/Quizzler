using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quizzler.Core.Study;

namespace Quizzler.App.Views;

/// <summary>
/// GitHub-style calendar: one square per study day, weeks as columns (Monday at the top), shaded by how many
/// questions were answered. Single sequential hue from the theme (HeatLevel0..4); every cell has a tooltip.
/// </summary>
public sealed class ActivityHeatmap : Border
{
    private const double Cell = 13, Gap = 3;

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<DayActivity>), typeof(ActivityHeatmap), new PropertyMetadata(null, (d, _) => ((ActivityHeatmap)d).Rebuild()));

    public IReadOnlyList<DayActivity>? Days
    {
        get => (IReadOnlyList<DayActivity>?)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    /// <summary>0 = no answers; 1-4 = quarters of the busiest day.</summary>
    public static int LevelFor(int answers, int max) => answers <= 0 || max <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(4.0 * answers / max), 1, 4);

    private void Rebuild()
    {
        if (Days is not { Count: > 0 } days)
        {
            Child = null;
            return;
        }

        var max = days.Max(d => d.Answers);
        var firstMonday = days[0].Day.AddDays(-(((int)days[0].Day.DayOfWeek + 6) % 7));
        var weeks = (days[^1].Day.DayNumber - firstMonday.DayNumber) / 7 + 1;
        const double labelWidth = 30, monthHeight = 16;

        var canvas = new Canvas { Width = labelWidth + weeks * (Cell + Gap), Height = monthHeight + 7 * (Cell + Gap) };

        // Weekday labels (Mon / Wed / Fri) and month labels where a new month starts.
        foreach (var (row, name) in new[] { (0, "Mon"), (2, "Wed"), (4, "Fri") })
            canvas.Children.Add(Label(name, 0, monthHeight + row * (Cell + Gap) - 1));
        var lastMonth = -1;
        for (var w = 0; w < weeks; w++)
        {
            var weekStart = firstMonday.AddDays(w * 7);
            if (weekStart.Month != lastMonth && weekStart.Day <= 7)
                canvas.Children.Add(Label(weekStart.ToString("MMM", CultureInfo.CurrentCulture), labelWidth + w * (Cell + Gap), 0));
            lastMonth = weekStart.Month;
        }

        foreach (var day in days)
        {
            var offset = day.Day.DayNumber - firstMonday.DayNumber;
            var level = LevelFor(day.Answers, max);
            var rect = new Border
            {
                Width = Cell,
                Height = Cell,
                CornerRadius = new CornerRadius(2.5),
                ToolTip = day.Answers == 0
                    ? $"{day.Day:ddd d MMM}: no study"
                    : $"{day.Day:ddd d MMM}: {day.Answers} answered, {100 * day.Correct / day.Answers}% right",
            };
            rect.SetResourceReference(BackgroundProperty, $"HeatLevel{level}Brush");
            Canvas.SetLeft(rect, labelWidth + offset / 7 * (Cell + Gap));
            Canvas.SetTop(rect, monthHeight + offset % 7 * (Cell + Gap));
            canvas.Children.Add(rect);
        }

        // Legend: Less □□□□□ More
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(labelWidth, 8, 0, 0) };
        legend.Children.Add(Label("Less", 0, 0, inCanvas: false));
        for (var level = 0; level <= 4; level++)
        {
            var swatch = new Border { Width = Cell, Height = Cell, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(Gap, 0, 0, 0) };
            swatch.SetResourceReference(BackgroundProperty, $"HeatLevel{level}Brush");
            legend.Children.Add(swatch);
        }
        var more = Label("More", 0, 0, inCanvas: false);
        more.Margin = new Thickness(Gap + 2, 0, 0, 0);
        legend.Children.Add(more);

        Child = new StackPanel { Children = { canvas, legend } };
    }

    private static TextBlock Label(string text, double x, double y, bool inCanvas = true)
    {
        var label = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        if (inCanvas)
        {
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
        }
        return label;
    }
}

/// <summary>
/// Upcoming reviews per day: thin single-series bars from a shared baseline, rounded data ends, hover tooltips,
/// and value labels only on today and the busiest day (not on every bar).
/// </summary>
public sealed class ForecastChart : Border
{
    private const double BarWidth = 22, Slot = 40, PlotHeight = 120;

    public static readonly DependencyProperty ForecastProperty = DependencyProperty.Register(
        nameof(Forecast), typeof(IReadOnlyList<ForecastDay>), typeof(ForecastChart), new PropertyMetadata(null, (d, _) => ((ForecastChart)d).Rebuild()));

    public IReadOnlyList<ForecastDay>? Forecast
    {
        get => (IReadOnlyList<ForecastDay>?)GetValue(ForecastProperty);
        set => SetValue(ForecastProperty, value);
    }

    private void Rebuild()
    {
        if (Forecast is not { Count: > 0 } days)
        {
            Child = null;
            return;
        }

        var max = Math.Max(days.Max(d => d.Due), 1);
        var busiest = days.Select((d, i) => (d.Due, i)).MaxBy(x => x.Due).i;
        const double labelSpace = 18, valueSpace = 18;
        var canvas = new Canvas { Width = days.Count * Slot, Height = valueSpace + PlotHeight + labelSpace };

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var x = i * Slot + (Slot - BarWidth) / 2;
            var height = day.Due == 0 ? 0 : Math.Max(3, PlotHeight * day.Due / max);
            var name = i == 0 ? "Today" : i == 1 ? "Tomorrow" : day.Day.ToString("dddd d MMM", CultureInfo.CurrentCulture);
            var tooltip = $"{name}: {day.Due} review{(day.Due == 1 ? "" : "s")} due" + (i == 0 ? " (including overdue)" : "");

            // Hit target is the whole column, bigger than the bar.
            var hit = new Border { Width = Slot, Height = valueSpace + PlotHeight, Background = Brushes.Transparent, ToolTip = tooltip };
            Canvas.SetLeft(hit, i * Slot);
            canvas.Children.Add(hit);

            if (height > 0)
            {
                var bar = new Border { Width = BarWidth, Height = height, CornerRadius = new CornerRadius(4, 4, 0, 0), IsHitTestVisible = false };
                bar.SetResourceReference(BackgroundProperty, "AccentFillBrush");
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, valueSpace + PlotHeight - height);
                canvas.Children.Add(bar);
            }

            if ((i == 0 || i == busiest) && day.Due > 0)
            {
                var value = Text(day.Due.ToString(CultureInfo.CurrentCulture), "TextPrimaryBrush", FontWeights.SemiBold);
                value.Width = Slot;
                value.TextAlignment = TextAlignment.Center;
                Canvas.SetLeft(value, i * Slot);
                Canvas.SetTop(value, valueSpace + PlotHeight - height - 16);
                canvas.Children.Add(value);
            }

            var label = Text(i == 0 ? "Today" : day.Day.ToString("ddd", CultureInfo.CurrentCulture)[..2], "MutedTextBrush", FontWeights.Normal);
            label.Width = Slot;
            label.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(label, i * Slot);
            Canvas.SetTop(label, valueSpace + PlotHeight + 3);
            canvas.Children.Add(label);
        }

        var baseline = new Border { Width = days.Count * Slot, Height = 1, IsHitTestVisible = false };
        baseline.SetResourceReference(BackgroundProperty, "BorderBrushSoft");
        Canvas.SetTop(baseline, valueSpace + PlotHeight);
        canvas.Children.Add(baseline);

        Child = canvas;
    }

    private static TextBlock Text(string text, string brushKey, FontWeight weight)
    {
        var block = new TextBlock { Text = text, FontSize = 11, FontWeight = weight };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }
}
