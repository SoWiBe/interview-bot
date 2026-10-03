using InterviewBot.Infrastructure.Telegram;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace InterviewBot.Host.Telegram;

#pragma warning disable CS9113 // зависимости пригодятся в реализации; убери pragma, когда допишешь
/// <summary>
/// Long polling: забирает апдейты у Telegram и отдаёт каждый в <see cref="BotUpdateDispatcher"/>.
/// </summary>
public sealed class TelegramPollingService(
    ITelegramBotClient bot,
    IServiceScopeFactory scopeFactory,
    IOptions<TelegramOptions> options,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // TODO(Алексей): реализовать цикл long polling — постановка в чате, этап 1.
        logger.LogWarning("Telegram polling is not implemented yet");
        return Task.CompletedTask;
    }
}
