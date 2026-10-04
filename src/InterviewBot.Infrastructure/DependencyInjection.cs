using System.ClientModel;
using System.Net.Http.Headers;
using Anthropic;
using InterviewBot.Infrastructure.Common;
using InterviewBot.Infrastructure.Content;
using InterviewBot.Infrastructure.Curriculum;
using InterviewBot.Infrastructure.Digests;
using InterviewBot.Infrastructure.GitHub;
using InterviewBot.Infrastructure.Persistence;
using InterviewBot.Infrastructure.Progress;
using InterviewBot.Infrastructure.Scheduling;
using InterviewBot.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using Quartz;
using Telegram.Bot;

namespace InterviewBot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<BotClock>();

        services.AddDbContext<BotDbContext>(o => o.UseNpgsql(postgres).UseSnakeCaseNamingConvention());

        services.AddOptions<CurriculumOptions>().Bind(configuration.GetSection(CurriculumOptions.SectionName));
        services.AddScoped<CurriculumSynchronizer>();

        services.AddTelegram(configuration);
        services.AddContentGeneration(configuration);
        services.AddGitHub(configuration);
        services.AddScheduling(postgres);

        services.AddSingleton<DigestGenerationGate>();
        services.AddScoped<DigestService>();
        services.AddScoped<PracticeService>();
        services.AddScoped<ProgressService>();
        services.AddScoped<UserSettingsService>();

        return services;
    }

    private static void AddTelegram(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // HttpClient через фабрику: пул хендлеров и DNS-ротация вместо new HttpClient() на каждый вызов.
        // Таймаут больше long polling, иначе каждый пустой getUpdates падал бы по таймауту.
        services.AddHttpClient("telegram", client => client.Timeout = TimeSpan.FromSeconds(100))
            // токен бота — часть URL (/bot<token>/method): стандартный логгер HttpClient записал бы его в логи
            .RemoveAllLoggers()
            .AddTypedClient<ITelegramBotClient>((httpClient, sp) =>
                new TelegramBotClient(sp.GetRequiredService<IOptions<TelegramOptions>>().Value.BotToken, httpClient));

        services.AddTransient<BotMessenger>();
        services.AddScoped<BotUpdateDispatcher>();
        services.AddSingleton<PollingHeartbeat>();
    }

    private static void AddContentGeneration(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration.GetSection(ContentOptions.SectionName).Get<ContentOptions>()?.Provider
            ?? new ContentOptions().Provider;

        // Оба SDK сами ретраят 429/5xx/сетевые ошибки — Polly поверх не вешаем, чтобы не получить ретраи в квадрате.
        // Валидируются настройки только выбранного провайдера: ключ второго не обязателен.
        switch (provider)
        {
            case ContentProvider.Anthropic:
                services.AddOptions<AnthropicOptions>()
                    .Bind(configuration.GetSection(AnthropicOptions.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                services.AddSingleton(sp => new AnthropicClient
                {
                    ApiKey = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value.ApiKey,
                    MaxRetries = 3,
                    Timeout = TimeSpan.FromMinutes(5),
                });
                services.AddSingleton<IContentGenerator, AnthropicContentGenerator>();
                break;

            case ContentProvider.OpenAICompatible:
                services.AddOptions<OpenAICompatibleOptions>()
                    .Bind(configuration.GetSection(OpenAICompatibleOptions.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                services.AddSingleton(sp =>
                {
                    var o = sp.GetRequiredService<IOptions<OpenAICompatibleOptions>>().Value;
                    return new ChatClient(o.Model, new ApiKeyCredential(o.ApiKey), new OpenAIClientOptions
                    {
                        Endpoint = new Uri(o.BaseUrl),
                        // генерация набора с размышлениями модели может идти дольше дефолтных 100 секунд
                        NetworkTimeout = TimeSpan.FromMinutes(5),
                        UserAgentApplicationId = "interview-bot",
                    });
                });
                services.AddSingleton<IContentGenerator, OpenAICompatibleContentGenerator>();
                break;

            default:
                throw new InvalidOperationException($"Unknown content provider '{provider}'");
        }
    }

    private static void AddGitHub(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GitHubOptions>()
            .Bind(configuration.GetSection(GitHubOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<GitHubClient>((sp, client) =>
            {
                client.BaseAddress = new Uri("https://api.github.com/");
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("interview-bot", "1.0"));
                client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

                var token = sp.GetRequiredService<IOptions<GitHubOptions>>().Value.Token;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
            })
            // ретраи с backoff + jitter, circuit breaker и таймауты попытки/запроса
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(20);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            });

        services.AddTransient<CodeSnippetProvider>();
    }

    private static void AddScheduling(this IServiceCollection services, string postgres)
    {
        services.AddQuartz(q =>
        {
            q.SchedulerName = "interview-bot";
            q.UsePersistentStore(store =>
            {
                // в JobDataMap только строки — данные джоба читаемы в БД и не зависят от сериализации типов
                store.UseProperties = true;
                store.UsePostgres(postgres);
                store.UseSystemTextJsonSerializer();
            });
        });
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
        services.AddSingleton<DigestScheduler>();
    }

    /// <summary>Миграции, синхронизация программы и расписание — до старта polling и Quartz.</summary>
    public static async Task InitializeInfrastructureAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DependencyInjection));

        await provider.GetRequiredService<BotDbContext>().Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Database migrated");

        await provider.GetRequiredService<CurriculumSynchronizer>().SyncAsync(cancellationToken);

        if (await provider.GetRequiredService<UserSettingsService>().GetAsync(cancellationToken) is { } settings)
        {
            await provider.GetRequiredService<DigestScheduler>().EnsureScheduledAsync(settings, cancellationToken);
        }
    }
}
