using Quizzler.Core.Study;

namespace Quizzler.Tests.Study;

public class NotificationPolicyTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(11);
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 11, 19, hour, minute, 0, Offset);

    private static readonly DueSummary Due = new(DueReviews: 3, NewAvailable: 2, NextDueAt: null);
    private static readonly DueSummary Nothing = new(0, 0, null);
    private static readonly NotificationSettings Defaults = new();

    private static bool Should(DueSummary? summary = null, NotificationSettings? settings = null, DateTimeOffset? now = null,
        DateTimeOffset? last = null, DateTimeOffset? snooze = null, bool studying = false) =>
        NotificationPolicy.ShouldNotify(summary ?? Due, settings ?? Defaults, now ?? At(10), last, snooze, studying);

    [Fact] public void Notifies_when_due_and_never_notified() => Assert.True(Should());
    [Fact] public void Not_when_nothing_due() => Assert.False(Should(summary: Nothing));
    [Fact] public void Not_when_disabled() => Assert.False(Should(settings: Defaults with { Enabled = false }));
    [Fact] public void Not_while_studying() => Assert.False(Should(studying: true));
    [Fact] public void Not_while_snoozed() => Assert.False(Should(snooze: At(11)));
    [Fact] public void After_snooze_expires() => Assert.True(Should(snooze: At(9, 59)));

    [Fact]
    public void Respects_reminder_interval()
    {
        Assert.False(Should(last: At(9, 30)));
        Assert.True(Should(last: At(9, 0)));
    }

    [Theory]
    [InlineData(21, false)]
    [InlineData(22, true)]
    [InlineData(23, true)]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void Quiet_hours_wrap_midnight(int hour, bool quiet)
    {
        Assert.Equal(quiet, NotificationPolicy.IsQuietTime(Defaults, At(hour)));
    }

    [Fact]
    public void Quiet_hours_within_a_day()
    {
        var lunch = Defaults with { QuietStartHour = 12, QuietEndHour = 14 };
        Assert.True(NotificationPolicy.IsQuietTime(lunch, At(13)));
        Assert.False(NotificationPolicy.IsQuietTime(lunch, At(14)));
    }

    [Fact]
    public void Equal_quiet_hours_means_none()
    {
        Assert.False(NotificationPolicy.IsQuietTime(Defaults with { QuietStartHour = 0, QuietEndHour = 0 }, At(0)));
    }

    [Fact]
    public void Next_morning_is_end_of_quiet_hours()
    {
        Assert.Equal(At(8).AddDays(1), NotificationPolicy.NextMorning(Defaults, At(15)));
        Assert.Equal(At(8), NotificationPolicy.NextMorning(Defaults, At(2)));
    }

    [Theory]
    [InlineData(3, 0, "3 questions due for review.")]
    [InlineData(1, 0, "1 question due for review.")]
    [InlineData(0, 1, "1 new question ready to learn.")]
    [InlineData(2, 5, "2 due for review and 5 new.")]
    public void Message_text(int reviews, int fresh, string expected)
    {
        Assert.Equal(expected, NotificationPolicy.Message(new DueSummary(reviews, fresh, null)));
    }
}
