using InterviewBot.Infrastructure.Common;
using InterviewBot.Infrastructure.Persistence;
using InterviewBot.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;

namespace InterviewBot.Infrastructure.Digests;

/// <summary>
/// Отправка одной задачи, at-most-once: сначала атомарный захват
/// <c>UPDATE practice_tasks SET sent_at = now WHERE id = @id AND sent_at IS NULL</c>, потом отправка.
/// Двойное нажатие «Следующая задача» или /today параллельно с кнопкой не пришлют задачу дважды.
/// </summary>
public sealed class TaskSender(BotDbContext db, BotMessenger messenger, BotClock clock, ILogger<TaskSender> logger)
{
    /// <param name="siblings">Все задачи того же набора — чтобы показать «1/3» и свернуть уже показанную схему.</param>
    /// <returns>false — задачу уже отправил кто-то другой.</returns>
    public async Task<bool> SendAsync(
        long chatId, PracticeTask task, IReadOnlyCollection<PracticeTask> siblings, string? header, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var claimed = await db.PracticeTasks
            .Where(t => t.Id == task.Id && t.SentAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.SentAt, now), cancellationToken);

        if (claimed == 0)
        {
            return false;
        }

        Task<int> SendTaskMessageAsync(bool collapseSchema) => messenger.SendHtmlAsync(
            chatId,
            MessageRenderer.Task(task, siblings.Count, header, collapseSchema),
            MessageRenderer.TaskKeyboard(task.Id),
            cancellationToken);

        int messageId;
        try
        {
            // пришла новая задача — незавершённое «жду решение» от прошлой больше не актуально,
            // иначе первый же вопрос по новой задаче ушёл бы на ревью как решение старой
            await db.PracticeTasks
                .Where(t => t.AwaitingSolutionSince != null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.AwaitingSolutionSince, (DateTimeOffset?)null), cancellationToken);

            var schemaAlreadyShown = !string.IsNullOrWhiteSpace(task.Schema)
                && siblings.Any(t => t.OrderInDigest < task.OrderInDigest && t.SentAt != null && t.Schema == task.Schema);

            try
            {
                messageId = await SendTaskMessageAsync(schemaAlreadyShown);
            }
            catch (ApiRequestException ex) when (schemaAlreadyShown && ex.ErrorCode == 400)
            {
                // свёрнутая цитата с кодом внутри — единственная нестандартная разметка в задаче:
                // если Telegram её не принял, сессия не должна застрять на этой задаче
                logger.LogWarning(ex, "Telegram rejected collapsed schema for task {TaskId}, sending it expanded", task.Id);
                messageId = await SendTaskMessageAsync(collapseSchema: false);
            }
        }
        catch
        {
            // CancellationToken.None: даже при остановке сервиса захват надо отпустить
            await db.PracticeTasks
                .Where(t => t.Id == task.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.SentAt, (DateTimeOffset?)null), CancellationToken.None);
            throw;
        }

        task.SentAt = now;
        task.TelegramMessageId = messageId;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
