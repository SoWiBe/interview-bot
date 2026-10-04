using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace InterviewBot.Infrastructure.Telegram;

/// <summary>Отправка HTML-сообщений с учётом лимита длины. Клавиатура цепляется к последнему куску.</summary>
public sealed class BotMessenger(ITelegramBotClient bot, ILogger<BotMessenger> logger)
{
    private const int MaxSendAttempts = 3;
    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    /// <returns>Id первого отправленного сообщения.</returns>
    public async Task<int> SendHtmlAsync(
        long chatId, IReadOnlyList<string> messages, InlineKeyboardMarkup? keyboard, CancellationToken cancellationToken)
    {
        var firstId = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var isLast = i == messages.Count - 1;
            var text = messages[i];
            var sent = await SendWithRetryAsync(
                ct => bot.SendMessage(
                    chatId,
                    text,
                    ParseMode.Html,
                    linkPreviewOptions: NoPreview,
                    replyMarkup: isLast ? keyboard : null,
                    cancellationToken: ct),
                cancellationToken);

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
        SendWithRetryAsync(
            ct => bot.SendMessage(chatId, text, linkPreviewOptions: NoPreview, cancellationToken: ct),
            cancellationToken);

    /// <summary>«Печатает…» — чистая косметика: сбой не должен мешать основной работе.</summary>
    public async Task TypingAsync(long chatId, CancellationToken cancellationToken)
    {
        try
        {
            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Failed to send typing action");
        }
    }

    /// <summary>
    /// Повтор только при сетевом обрыве (соединение закрылось, таймаут). Ответ API с ошибкой
    /// (ApiRequestException: неверный HTML, бот заблокирован) повтором не лечится — пробрасываем сразу.
    /// Риск: если обрыв случился после того, как Telegram принял сообщение, оно придёт дважды —
    /// для ответа бота это лучше, чем потерять ревью.
    /// </summary>
    private async Task<T> SendWithRetryAsync<T>(Func<CancellationToken, Task<T>> send, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await send(cancellationToken);
            }
            catch (Exception ex) when (attempt < MaxSendAttempts && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(attempt * 2);
                logger.LogWarning(ex, "Telegram send failed (attempt {Attempt}), retry in {RetryDelay}", attempt, delay);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        ApiRequestException { ErrorCode: 429 or >= 500 } => true,
        ApiRequestException => false,
        RequestException or HttpRequestException or TaskCanceledException => true,
        _ => false,
    };
}
