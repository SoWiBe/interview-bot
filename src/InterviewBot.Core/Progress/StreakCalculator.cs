namespace InterviewBot.Core.Progress;

public enum DayKind
{
    /// <summary>В этот день я что-то сделал: отметил тему или отправил решение.</summary>
    Active,

    /// <summary>День паузы (/pause): серию не увеличивает, но и не рвёт.</summary>
    Paused,
}

public static class StreakCalculator
{
    /// <summary>
    /// Серия дней подряд с активностью, считая назад от сегодня.
    /// Сегодняшний день без активности серию не рвёт — день ещё не закончился.
    /// </summary>
    public static int Calculate(IReadOnlyDictionary<DateOnly, DayKind> days, DateOnly today)
    {
        var streak = 0;
        var day = today;

        if (!days.ContainsKey(today))
        {
            day = today.AddDays(-1);
        }

        while (days.TryGetValue(day, out var kind))
        {
            if (kind == DayKind.Active)
            {
                streak++;
            }

            day = day.AddDays(-1);
        }

        return streak;
    }
}
