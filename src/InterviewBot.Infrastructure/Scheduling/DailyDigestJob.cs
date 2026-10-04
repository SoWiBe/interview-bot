using InterviewBot.Infrastructure.Digests;
using InterviewBot.Infrastructure.Progress;
using InterviewBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging;
using Quartz;

namespace InterviewBot.Infrastructure.Scheduling;

/// <summary>
/// Утренний набор. Сам по себе джоб может сработать дважды (misfire + обычный запуск, ретрай, /today параллельно) —
/// от двойной отправки защищает не он, а DigestDelivery с атомарным захватом в БД.
/// DisallowConcurrentExecution — лишь дополнительная страховка внутри одного планировщика.
/// </summary>
[DisallowConcurrentExecution]
public sealed class DailyDigestJob(
    UserSettingsService settingsService,
    DigestService digests,
    DigestScheduler scheduler,
    BotMessenger messenger,
    ILogger<DailyDigestJob> logger) : IJob
{
    public const string AttemptKey = "attempt";
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var attempt = int.TryParse(context.MergedJobDataMap.GetString(AttemptKey), out var parsed) ? parsed : 1;

        var settings = await settingsService.GetAsync(cancellationToken);
        if (settings is null)
        {
            logger.LogInformation("Daily digest skipped: user is not registered yet");
            return;
        }

        try
        {
            await digests.RunDailyAsync(settings, manual: false, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Не JobExecutionException(refireImmediately): при лежащем API это была бы горячая петля
            if (attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "Daily digest attempt {Attempt} failed, retry in {RetryDelay}", attempt, RetryDelay);
                await scheduler.ScheduleRetryAsync(attempt + 1, RetryDelay, cancellationToken);
                return;
            }

            logger.LogError(ex, "Daily digest failed after {Attempts} attempts", attempt);
            await messenger.SendTextAsync(
                settings.ChatId,
                "⚠️ Не получилось подготовить утренний набор (подробности в логах). Попробуй позже: /today",
                cancellationToken);
        }
    }
}
