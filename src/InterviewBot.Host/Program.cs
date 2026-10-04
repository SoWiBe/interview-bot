using InterviewBot.Host.Telegram;
using InterviewBot.Infrastructure;
using InterviewBot.Infrastructure.Telegram;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddHostedService<TelegramPollingService>();

    builder.Services.AddHealthChecks()
        .AddNpgSql(builder.Configuration.GetConnectionString("Postgres")!, name: "postgres", tags: ["ready"])
        .AddCheck<TelegramPollingHealthCheck>("telegram-polling", tags: ["ready"]);

    var app = builder.Build();

    await app.Services.InitializeInfrastructureAsync();

    app.UseSerilogRequestLogging();

    // liveness: процесс жив и отвечает, внешние зависимости не проверяем
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    // readiness: БД доступна и polling получает апдейты
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
