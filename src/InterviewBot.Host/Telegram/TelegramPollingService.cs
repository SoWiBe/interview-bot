using InterviewBot.Infrastructure.Telegram;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace InterviewBot.Host.Telegram;

/// <summary>
/// Long polling: забирает апдейты у Telegram и отдаёт каждый в <see cref="BotUpdateDispatcher"/>.
/// </summary>
public sealed class TelegramPollingService(
    ITelegramBotClient bot,
    IServiceScopeFactory scopeFactory,
    IOptions<TelegramOptions> options,
    PollingHeartbeat heartbeat,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    private const int MaxCommandAttempts = 5;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly UpdateType[] AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int? offset = null;
        var consecutiveFailures = 0;
        var commandsRegistered = false;
        var commandAttempts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            Update[] updates;
            try
            {
                // timeout > 0 — это и есть long polling: Telegram держит запрос, пока не появятся апдейты
                updates = await bot.GetUpdates(
                    offset,
                    limit: 100,
                    timeout: options.Value.PollingTimeoutSeconds,
                    allowedUpdates: AllowedUpdates,
                    cancellationToken: stoppingToken);
                consecutiveFailures = 0;
                heartbeat.Beat();

                // меню команд регистрируем, как только связь с Telegram есть; не вышло — ещё несколько попыток
                // на следующих итерациях, но не бесконечно, чтобы не тормозить polling
                if (!commandsRegistered && commandAttempts++ < MaxCommandAttempts)
                {
                    commandsRegistered = await BotUpdateDispatcher.TryRegisterCommandsAsync(bot, logger, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var delay = GetRetryDelay(ex, consecutiveFailures);
                logger.LogWarning(ex, "Failed to get updates, retry in {RetryDelay}", delay);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            // Последовательно, а не параллельно: важен порядок сообщений, а scoped-сервисы не потокобезопасны
            foreach (var update in updates)
            {
                try
                {
                    await HandleUpdateAsync(update, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // offset не сдвинут — апдейт придёт снова после перезапуска
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to handle update {UpdateId}", update.Id);
                }

                // Сдвигаем и после ошибки: иначе «ядовитый» апдейт будет обрабатываться бесконечно
                offset = update.Id + 1;
            }
        }

        logger.LogInformation("Telegram polling stopped");
    }

    private async Task HandleUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<BotUpdateDispatcher>();
        await dispatcher.HandleAsync(update, cancellationToken);
    }

    private static TimeSpan GetRetryDelay(Exception ex, int consecutiveFailures)
    {
        // 429 Too Many Requests: Telegram сам говорит, сколько ждать
        if (ex is ApiRequestException { Parameters.RetryAfter: { } retryAfter })
        {
            return TimeSpan.FromSeconds(retryAfter);
        }

        // экспоненциальная задержка 1, 2, 4, 8... секунд, но не больше минуты
        var seconds = Math.Pow(2, Math.Min(consecutiveFailures - 1, 6));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }
}
