using InterviewBot.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Quartz;

namespace InterviewBot.Infrastructure.Scheduling;

public sealed class DigestScheduler(ISchedulerFactory schedulerFactory, ILogger<DigestScheduler> logger)
{
    public static readonly JobKey JobKey = new("daily-digest", "digest");
    private static readonly TriggerKey DailyTriggerKey = new("daily-digest-trigger", "digest");

    /// <summary>
    /// Создаёт или обновляет ежедневный триггер по настройкам пользователя.
    /// Если триггер уже такой, как нужно, — не трогаем: пересоздание сбросило бы next_fire_time,
    /// и Quartz не узнал бы о пропущенном (misfire) запуске, пока сервис был выключен.
    /// </summary>
    public async Task EnsureScheduledAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        var cron = $"0 {settings.SendTime.Minute} {settings.SendTime.Hour} ? * *";
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);

        if (await scheduler.GetTrigger(DailyTriggerKey, cancellationToken) is ICronTrigger existing
            && existing.CronExpressionString == cron
            && existing.TimeZone.Id == zone.Id)
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

        await scheduler.ScheduleJob(job, [trigger], replace: true, cancellationToken);
        logger.LogInformation("Daily digest scheduled at {SendTime} {TimeZone}", settings.SendTime, settings.TimeZoneId);
    }

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
