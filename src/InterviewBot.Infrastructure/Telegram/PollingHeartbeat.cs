using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace InterviewBot.Infrastructure.Telegram;

/// <summary>
/// Время последнего успешного getUpdates. Без этого health check показывал бы Healthy,
/// даже если цикл polling мёртв, а процесс жив.
/// </summary>
public sealed class PollingHeartbeat(TimeProvider time)
{
    private long _lastSuccessTicks = time.GetUtcNow().UtcTicks;

    public DateTimeOffset LastSuccess => new(Interlocked.Read(ref _lastSuccessTicks), TimeSpan.Zero);

    public void Beat() => Interlocked.Exchange(ref _lastSuccessTicks, time.GetUtcNow().UtcTicks);
}

public sealed class TelegramPollingHealthCheck(PollingHeartbeat heartbeat, TimeProvider time, IOptions<TelegramOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // один long polling запрос + максимальная пауза между ретраями + запас
        var threshold = TimeSpan.FromSeconds(options.Value.PollingTimeoutSeconds * 2 + 90);
        var silence = time.GetUtcNow() - heartbeat.LastSuccess;

        return Task.FromResult(silence <= threshold
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"No successful getUpdates for {silence.TotalSeconds:0} s"));
    }
}
