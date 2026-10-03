using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace InterviewBot.Infrastructure.Telegram;

/// <summary>
/// Обрабатывает один апдейт от Telegram. Регистрируется как scoped: на следующих этапах
/// сюда придёт DbContext, поэтому на каждый апдейт нужен свой scope.
/// </summary>
public sealed class BotUpdateDispatcher(
    ITelegramBotClient bot,
    IOptions<TelegramOptions> options,
    ILogger<BotUpdateDispatcher> logger)
{
    public async Task HandleAsync(Update update, CancellationToken cancellationToken)
    {
        var sender = update.Message?.From ?? update.CallbackQuery?.From;
        if (sender is null || sender.Id != options.Value.AllowedUserId)
        {
            logger.LogWarning("Ignoring update {UpdateId} from unknown user {UserId}", update.Id, sender?.Id);
            return;
        }

        if (update.Message is { Text: { } text } message)
        {
            await HandleCommandAsync(message, text, cancellationToken);
        }
    }

    private async Task HandleCommandAsync(Message message, string text, CancellationToken cancellationToken)
    {
        var command = text.Split(' ', 2)[0];

        switch (command)
        {
            case "/start":
                logger.LogInformation("User {UserId} started the bot in chat {ChatId}", message.From!.Id, message.Chat.Id);
                await bot.SendMessage(
                    message.Chat.Id,
                    "Привет! Я твой помощник для подготовки к собеседованиям. Каждое утро буду присылать теорию, код из реальных репозиториев и задачи.",
                    cancellationToken: cancellationToken);
                break;

            default:
                await bot.SendMessage(message.Chat.Id, "Не знаю такой команды.", cancellationToken: cancellationToken);
                break;
        }
    }
}
