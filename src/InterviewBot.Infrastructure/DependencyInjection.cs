using InterviewBot.Infrastructure.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace InterviewBot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTelegram(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // HttpClient через фабрику: пул хендлеров и DNS-ротация вместо new HttpClient() на каждый вызов
        services.AddHttpClient("telegram")
            .AddTypedClient<ITelegramBotClient>((httpClient, sp) =>
                new TelegramBotClient(sp.GetRequiredService<IOptions<TelegramOptions>>().Value.BotToken, httpClient));

        services.AddScoped<BotUpdateDispatcher>();

        return services;
    }
}
