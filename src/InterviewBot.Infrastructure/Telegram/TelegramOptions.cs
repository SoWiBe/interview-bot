using System.ComponentModel.DataAnnotations;

namespace InterviewBot.Infrastructure.Telegram;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    [Required]
    public string BotToken { get; init; } = string.Empty;

    /// <summary>Telegram ID единственного пользователя, которому бот отвечает.</summary>
    [Range(1, long.MaxValue)]
    public long AllowedUserId { get; init; }

    /// <summary>Сколько секунд Telegram держит long polling запрос, если новых апдейтов нет.</summary>
    [Range(1, 50)]
    public int PollingTimeoutSeconds { get; init; } = 30;
}
