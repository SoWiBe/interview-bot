using InterviewBot.Core.Schedule;
using InterviewBot.Infrastructure.Common;
using InterviewBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InterviewBot.Infrastructure.Progress;

public sealed class UserSettingsService(BotDbContext db, BotClock clock)
{
    /// <summary>Бот личный: в таблице не больше одной строки.</summary>
    public Task<UserSettings?> GetAsync(CancellationToken cancellationToken) =>
        db.UserSettings.OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    public async Task<UserSettings> RegisterAsync(long userId, long chatId, CancellationToken cancellationToken)
    {
        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.TelegramUserId == userId, cancellationToken);
        if (settings is null)
        {
            settings = new UserSettings { TelegramUserId = userId, CreatedAt = clock.UtcNow };
            db.UserSettings.Add(settings);
        }

        settings.ChatId = chatId;
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task SetSendTimeAsync(UserSettings settings, TimeOnly time, CancellationToken cancellationToken)
    {
        settings.SendTime = time;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetWeekendModeAsync(UserSettings settings, WeekendMode mode, CancellationToken cancellationToken)
    {
        settings.WeekendMode = mode;
        await db.SaveChangesAsync(cancellationToken);
    }
}
