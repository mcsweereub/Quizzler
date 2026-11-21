using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quizzler.Core.Study;

namespace Quizzler.App.ViewModels;

public sealed record StatTile(string Label, string Value, string Detail);

/// <param name="BarWidth">Width of the filled bar in device-independent pixels (track is <see cref="StatsViewModel.BarTrack"/>).</param>
public sealed record RetentionRow(string Label, double BarWidth, string RateText, string CountText);

public sealed record TopicRow(string Topic, double SeenWidth, double MatureWidth, string SeenText, string AccuracyText);

public sealed record LeechRow(string Id, string Prompt, string Detail, string? LastWrong)
{
    public bool HasLastWrong => !string.IsNullOrWhiteSpace(LastWrong);
}

/// <summary>Read-only stats screen over <see cref="StatsReport"/>. Refreshes on demand.</summary>
public sealed class StatsViewModel : ObservableObject
{
    public const double BarTrack = 220;

    private readonly QuizService _service;
    private readonly Func<IReadOnlyDictionary<string, string>?> _hashes;
    private StatsReport? _report;

    public StatsViewModel(QuizService service, Func<IReadOnlyDictionary<string, string>?>? currentHashes = null)
    {
        _service = service;
        _hashes = currentHashes ?? (() => null);
        RefreshCommand = new RelayCommand(Refresh);
        Refresh();
    }

    public RelayCommand RefreshCommand { get; }

    public StatsReport? Report
    {
        get => _report;
        private set
        {
            if (!SetProperty(ref _report, value)) return;
            OnPropertyChanged(string.Empty); // everything below derives from the report
        }
    }

    public bool HasAnswers => Report is { TotalAnswers: > 0 };
    public bool NoAnswers => !HasAnswers;

    public IReadOnlyList<StatTile> Tiles => Report is not { } r ? [] :
    [
        new("Streak", DayCount(r.CurrentStreak),
            r.CurrentStreak > 0 && !r.StudiedToday ? "Study today to keep it" : $"Best: {DayCount(r.LongestStreak)}"),
        new("Today", r.AnsweredToday.ToString(CultureInfo.CurrentCulture),
            r.AnsweredToday == 0 ? "answered" : $"answered · {Percent(r.CorrectToday, r.AnsweredToday)} right"),
        new("Learned", $"{r.Seen} / {r.QuestionsTotal}", $"{r.Typed} typed · {r.Mature} well learned"),
        new("Accuracy", r.RecentAccuracy is { } a ? a.ToString("P0", CultureInfo.CurrentCulture) : "–",
            $"last 30 days · {r.RecentAnswers} answers"),
    ];

    public IReadOnlyList<DayActivity>? Days => Report?.Days;
    public IReadOnlyList<ForecastDay>? Forecast => Report?.Forecast;
    public string ForecastNote => Report is not { } r ? "" :
        $"{r.Forecast.Sum(f => f.Due)} reviews in the next two weeks, plus up to {r.NewPerDay} new questions a day.";

    public bool HasRetention => Report?.Retention.Any(b => b.Reviews > 0) == true;
    public bool NoRetention => !HasRetention;
    public IReadOnlyList<RetentionRow> Retention => Report?.Retention
        .Select(b => new RetentionRow(
            b.Label,
            (b.Rate ?? 0) * BarTrack,
            b.Rate is { } rate ? rate.ToString("P0", CultureInfo.CurrentCulture) : "–",
            b.Reviews == 0 ? "no reviews yet" : $"of {b.Reviews}"))
        .ToList() ?? [];

    public IReadOnlyList<TopicRow> Topics => Report?.Topics
        .Select(t => new TopicRow(
            t.Topic,
            BarTrack * t.Seen / Math.Max(t.Questions, 1),
            BarTrack * t.Mature / Math.Max(t.Questions, 1),
            $"{t.Seen} / {t.Questions} seen",
            t.RecentAccuracy is { } a ? $"{a.ToString("P0", CultureInfo.CurrentCulture)} right · {t.RecentAnswers} answers" : "no answers in 30 days"))
        .ToList() ?? [];

    public bool HasLeeches => Report?.Leeches.Count > 0;
    public bool NoLeeches => !HasLeeches;
    public IReadOnlyList<LeechRow> Leeches => Report?.Leeches
        .Select(l => new LeechRow(
            l.Question.Id,
            l.Question.Prompt,
            $"Wrong {l.Wrong} of {l.Attempts}" + (l.Overrides > 0 ? $" · you overrode the grader {l.Overrides}×" : ""),
            l.LastWrongResponse is null ? null : $"Last wrong answer: {Shorten(l.LastWrongResponse)}"))
        .ToList() ?? [];

    public string HabitsText
    {
        get
        {
            if (Report is not { } r) return "";
            var parts = new List<string>();
            var rem = r.Reminders;
            parts.Add(rem.Shown == 0 && rem.Started == 0
                ? "No reminders in the last 30 days."
                : $"Last 30 days: {Count(rem.Shown, "reminder")} shown, {rem.Started} opened a quiz, {rem.Snoozed} snoozed.");
            if (r.MedianAnswerTime is { } t) parts.Add($"Typical time per question: {t.TotalSeconds:0} s.");
            if (r.RecentOverrides > 0) parts.Add($"You overrode the grader {Count(r.RecentOverrides, "time")} (those questions may need more accepted answers).");
            return string.Join("  ", parts);
        }
    }

    public void Refresh() => Report = _service.GetStats(_hashes());

    private static string DayCount(int n) => n == 1 ? "1 day" : $"{n} days";
    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
    private static string Percent(int part, int whole) => whole == 0 ? "–" : ((double)part / whole).ToString("P0", CultureInfo.CurrentCulture);
    private static string Shorten(string s)
    {
        var line = s.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : line[..77] + "…";
    }
}
