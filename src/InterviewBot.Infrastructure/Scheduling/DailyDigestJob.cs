using InterviewBot.Infrastructure.Digests;
using InterviewBot.Infrastructure.Progress;
using InterviewBot.Infrastructure.Telegram;
using Microsoft.Extensions.Logging;
using Quartz;

namespace InterviewBot.Infrastructure.Scheduling;

/// <summary>
/// Утренняя сессия: за <see cref="DigestScheduler.PrepareLead"/> до времени отправки готовит набор, в назначенное время отправляет.
/// Сам по себе джоб может сработать дважды (misfire + обычный запуск, ретрай, /today параллельно) —
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
    public const string ModeKey = "mode";
    public const string PrepareMode = "prepare";
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        // TryGetString, а не GetString: у ежедневных триггеров этих ключей нет, и GetString на отсутствующем ключе бросает
        var data = context.MergedJobDataMap;
        var attempt = data.TryGetString(AttemptKey, out var rawAttempt) && int.TryParse(rawAttempt, out var parsed) ? parsed : 1;

        var settings = await settingsService.GetAsync(cancellationToken);
        if (settings is null)
        {
            logger.LogInformation("Daily digest skipped: user is not registered yet");
            return;
        }

        if (data.TryGetString(ModeKey, out var mode) && mode == PrepareMode)
        {
            try
            {
                await digests.PrepareDailyAsync(settings, DigestScheduler.PrepareLead, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // не страшно: основной запуск в назначенное время сгенерирует набор сам, со своими ретраями
                logger.LogWarning(ex, "Failed to prepare daily digest in advance");
            }

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
