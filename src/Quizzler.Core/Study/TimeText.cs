namespace Quizzler.Core.Study;

public static class TimeText
{
    /// <summary>"now", "in 10 min", "in 5 h", "in 6 days".</summary>
    public static string Until(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "now",
        { TotalHours: < 1 } => $"in {(int)Math.Ceiling(span.TotalMinutes)} min",
        { TotalDays: < 1 } => $"in {(int)Math.Round(span.TotalHours)} h",
        { TotalDays: < 1.5 } => "tomorrow",
        _ => $"in {(int)Math.Round(span.TotalDays)} days",
    };
}
