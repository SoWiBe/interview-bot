using InterviewBot.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Quartz;

namespace InterviewBot.Infrastructure.Scheduling;

public sealed class DigestScheduler(ISchedulerFactory schedulerFactory, ILogger<DigestScheduler> logger)
{
    public static readonly JobKey JobKey = new("daily-digest", "digest");
    private static readonly TriggerKey DailyTriggerKey = new("daily-digest-trigger", "digest");
    private static readonly TriggerKey PrepareTriggerKey = new("daily-digest-prepare-trigger", "digest");

    /// <summary>За сколько до времени отправки начинать генерацию: она занимает минуты, а сессия должна прийти вовремя.</summary>
    public static readonly TimeSpan PrepareLead = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Создаёт или обновляет ежедневный триггер по настройкам пользователя.
    /// Если триггер уже такой, как нужно, — не трогаем: пересоздание сбросило бы next_fire_time,
    /// и Quartz не узнал бы о пропущенном (misfire) запуске, пока сервис был выключен.
    /// </summary>
    public async Task EnsureScheduledAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        var cron = Cron(settings.SendTime);
        // TimeOnly.Add переходит через полночь: для отправки в 00:05 подготовка встанет на 23:50
        var prepareCron = Cron(settings.SendTime.Add(-PrepareLead));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);

        if (await IsScheduledAsync(scheduler, DailyTriggerKey, cron, zone, cancellationToken)
            && await IsScheduledAsync(scheduler, PrepareTriggerKey, prepareCron, zone, cancellationToken))
        {
            return;
        }

        var job = JobBuilder.Create<DailyDigestJob>()
            .WithIdentity(JobKey)
            .StoreDurably()
            .Build();

        // Каждый день: будни/выходные/пауза решает сам джоб — так /settings меняет одно поле, а не набор cron'ов.
        // FireAndProceed: если сервис лежал в момент запуска, после старта джоб выполнится один раз.
        var trigger = TriggerBuilder.Create()
            .WithIdentity(DailyTriggerKey)
            .ForJob(JobKey)
            .WithCronSchedule(cron, b => b.InTimeZone(zone).WithMisfireHandlingInstructionFireAndProceed())
            .Build();

        // Подготовка: если сервис лежал, навёрстывать её незачем — основной запуск сгенерирует набор сам
        var prepareTrigger = TriggerBuilder.Create()
            .WithIdentity(PrepareTriggerKey)
            .ForJob(JobKey)
            .UsingJobData(DailyDigestJob.ModeKey, DailyDigestJob.PrepareMode)
            .WithCronSchedule(prepareCron, b => b.InTimeZone(zone).WithMisfireHandlingInstructionDoNothing())
            .Build();

        await scheduler.ScheduleJob(job, [trigger, prepareTrigger], replace: true, cancellationToken);
        logger.LogInformation("Daily digest scheduled at {SendTime} {TimeZone}", settings.SendTime, settings.TimeZoneId);
    }

    private static string Cron(TimeOnly time) => $"0 {time.Minute} {time.Hour} ? * *";

    private static async Task<bool> IsScheduledAsync(
        IScheduler scheduler, TriggerKey key, string cron, TimeZoneInfo zone, CancellationToken cancellationToken) =>
        await scheduler.GetTrigger(key, cancellationToken) is ICronTrigger existing
        && existing.CronExpressionString == cron
        && existing.TimeZone.Id == zone.Id;

    public async Task ScheduleRetryAsync(int attempt, TimeSpan delay, CancellationToken cancellationToken)
    {
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"daily-digest-retry-{Guid.NewGuid():N}", "digest")
            .ForJob(JobKey)
            .UsingJobData(DailyDigestJob.AttemptKey, attempt.ToString())
            .StartAt(DateTimeOffset.UtcNow.Add(delay))
            .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow())
            .Build();

        await scheduler.ScheduleJob(trigger, cancellationToken);
    }
}
