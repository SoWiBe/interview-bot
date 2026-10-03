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

    var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");

    builder.Services.AddHealthChecks()
        .AddNpgSql(postgresConnectionString, name: "postgres", tags: ["ready"]);

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    // liveness: процесс жив и отвечает, внешние зависимости не проверяем
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    // readiness: зависимости доступны, можно работать
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
