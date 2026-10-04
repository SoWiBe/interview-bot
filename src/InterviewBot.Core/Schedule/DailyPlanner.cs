namespace InterviewBot.Core.Schedule;

public enum WeekendMode
{
    /// <summary>В выходные — одна задача-повторение без новой теории (по умолчанию).</summary>
    ReviewTask,

    /// <summary>В выходные — полный набор, как в будни.</summary>
    Full,

    /// <summary>В выходные ничего не присылать.</summary>
    Off,
}

public enum DailyPlan
{
    /// <summary>Теория + код из репозитория + две задачи.</summary>
    Full,

    /// <summary>Одна задача-повторение.</summary>
    ReviewTask,

    SkipPaused,
    SkipWeekend,
}

public static class DailyPlanner
{
    public static DailyPlan Plan(DateOnly today, WeekendMode weekendMode, DateOnly? pausedUntil)
    {
        if (pausedUntil is { } until && today <= until)
        {
            return DailyPlan.SkipPaused;
        }

        if (today.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
        {
            return DailyPlan.Full;
        }

        return weekendMode switch
        {
            WeekendMode.Full => DailyPlan.Full,
            WeekendMode.ReviewTask => DailyPlan.ReviewTask,
            WeekendMode.Off => DailyPlan.SkipWeekend,
            _ => throw new ArgumentOutOfRangeException(nameof(weekendMode), weekendMode, null),
        };
    }
}
