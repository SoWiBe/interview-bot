using InterviewBot.Core.Progress;
using InterviewBot.Core.Schedule;

namespace InterviewBot.Core.Tests;

public class StreakCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static Dictionary<DateOnly, DayKind> Days(params (int offset, DayKind kind)[] days) =>
        days.ToDictionary(d => Today.AddDays(d.offset), d => d.kind);

    [Test]
    public void ConsecutiveActiveDays_AreCounted()
    {
        var days = Days((0, DayKind.Active), (-1, DayKind.Active), (-2, DayKind.Active), (-4, DayKind.Active));

        Assert.That(StreakCalculator.Calculate(days, Today), Is.EqualTo(3));
    }

    [Test]
    public void TodayWithoutActivity_DoesNotBreakStreak()
    {
        var days = Days((-1, DayKind.Active), (-2, DayKind.Active));

        Assert.That(StreakCalculator.Calculate(days, Today), Is.EqualTo(2));
    }

    [Test]
    public void PausedDays_DoNotBreakAndDoNotCount()
    {
        var days = Days((0, DayKind.Active), (-1, DayKind.Paused), (-2, DayKind.Paused), (-3, DayKind.Active));

        Assert.That(StreakCalculator.Calculate(days, Today), Is.EqualTo(2));
    }

    [Test]
    public void MissedDay_BreaksStreak()
    {
        var days = Days((0, DayKind.Active), (-2, DayKind.Active));

        Assert.That(StreakCalculator.Calculate(days, Today), Is.EqualTo(1));
    }

    [Test]
    public void NoActivity_IsZero()
    {
        Assert.That(StreakCalculator.Calculate(new Dictionary<DateOnly, DayKind>(), Today), Is.Zero);
    }
}

public class DailyPlannerTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static readonly DateOnly Saturday = new(2026, 10, 10);

    [Test]
    public void Weekday_IsFull_RegardlessOfWeekendMode()
    {
        Assert.That(DailyPlanner.Plan(Monday, WeekendMode.Off, null), Is.EqualTo(DailyPlan.Full));
    }

    [TestCase(WeekendMode.ReviewTask, DailyPlan.ReviewTask)]
    [TestCase(WeekendMode.Full, DailyPlan.Full)]
    [TestCase(WeekendMode.Off, DailyPlan.SkipWeekend)]
    public void Weekend_FollowsMode(WeekendMode mode, DailyPlan expected)
    {
        Assert.That(DailyPlanner.Plan(Saturday, mode, null), Is.EqualTo(expected));
    }

    [Test]
    public void Pause_IsInclusive()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DailyPlanner.Plan(Monday, WeekendMode.Full, pausedUntil: Monday), Is.EqualTo(DailyPlan.SkipPaused));
            Assert.That(DailyPlanner.Plan(Monday.AddDays(1), WeekendMode.Full, pausedUntil: Monday), Is.EqualTo(DailyPlan.Full));
        });
    }
}
