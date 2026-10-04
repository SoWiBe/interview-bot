using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace InterviewBot.Infrastructure.Telegram;

/// <summary>Отправка HTML-сообщений с учётом лимита длины. Клавиатура цепляется к последнему куску.</summary>
public sealed class BotMessenger(ITelegramBotClient bot)
{
    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    /// <returns>Id первого отправленного сообщения.</returns>
    public async Task<int> SendHtmlAsync(
        long chatId, IReadOnlyList<string> messages, InlineKeyboardMarkup? keyboard, CancellationToken cancellationToken)
    {
        var firstId = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var isLast = i == messages.Count - 1;
            var sent = await bot.SendMessage(
                chatId,
                messages[i],
                ParseMode.Html,
                linkPreviewOptions: NoPreview,
                replyMarkup: isLast ? keyboard : null,
                cancellationToken: cancellationToken);

            if (i == 0)
            {
                firstId = sent.MessageId;
            }
        }

        return firstId;
    }

    public Task<int> SendHtmlAsync(long chatId, string html, InlineKeyboardMarkup? keyboard, CancellationToken cancellationToken) =>
        SendHtmlAsync(chatId, TelegramHtml.Pack(html.Split(TelegramHtml.BlockSeparator)), keyboard, cancellationToken);

    public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken) =>
        bot.SendMessage(chatId, text, linkPreviewOptions: NoPreview, cancellationToken: cancellationToken);

    public Task TypingAsync(long chatId, CancellationToken cancellationToken) =>
        bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: cancellationToken);
}
