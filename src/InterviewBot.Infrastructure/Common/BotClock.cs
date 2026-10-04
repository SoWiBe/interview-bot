namespace InterviewBot.Infrastructure.Common;

/// <summary>Время бота: «сегодня» считается в часовом поясе пользователя, а не сервера.</summary>
public sealed class BotClock(TimeProvider time)
{
    public DateTimeOffset UtcNow => time.GetUtcNow();

    public DateOnly Today(string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
    }
}
