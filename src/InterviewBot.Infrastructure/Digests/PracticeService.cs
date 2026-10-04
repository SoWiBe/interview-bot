using InterviewBot.Infrastructure.Common;
using InterviewBot.Infrastructure.Content;
using InterviewBot.Infrastructure.Persistence;
using InterviewBot.Infrastructure.Progress;
using InterviewBot.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;

namespace InterviewBot.Infrastructure.Digests;

public sealed class PracticeService(
    BotDbContext db,
    IContentGenerator generator,
    BotMessenger messenger,
    ProgressService progress,
    BotClock clock)
{
    /// <summary>
    /// Находит задачу для ответа: по reply на сообщение с задачей, иначе — последнюю отправленную нерешённую.
    /// </summary>
    public async Task<PracticeTask?> FindTaskAsync(int? replyToMessageId, CancellationToken cancellationToken)
    {
        if (replyToMessageId is { } messageId)
        {
            var replied = await db.PracticeTasks.FirstOrDefaultAsync(t => t.TelegramMessageId == messageId, cancellationToken);
            if (replied is not null)
            {
                return replied;
            }
        }

        return await db.PracticeTasks
            .Where(t => t.SentAt != null && t.SolutionRevealedAt == null)
            .OrderByDescending(t => t.SentAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task ReviewAttemptAsync(UserSettings settings, PracticeTask task, string answer, CancellationToken cancellationToken)
    {
        await messenger.SendTextAsync(settings.ChatId, $"🔎 Смотрю решение задачи «{task.Title}»…", cancellationToken);
        await messenger.TypingAsync(settings.ChatId, cancellationToken);

        var view = new PracticeTaskView(
            task.Language == TaskLanguage.Sql ? "sql" : "csharp", task.Statement, task.ExpectedQuestions, task.ReferenceSolution);
        var review = await generator.ReviewAttemptAsync(view, answer, cancellationToken);
        var reviewMessages = MessageRenderer.Review(review, task.Language);

        db.TaskAttempts.Add(new TaskAttempt
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SubmittedAt = clock.UtcNow,
            Answer = answer,
            AskedClarifyingQuestions = review.AskedClarifyingQuestions,
            Score = Math.Clamp(review.Score, 1, 5),
            ReviewHtml = string.Join(TelegramHtml.BlockSeparator, reviewMessages),
        });
        await db.SaveChangesAsync(cancellationToken);
        await progress.MarkActiveAsync(clock.Today(settings.TimeZoneId), cancellationToken);

        var keyboard = task.SolutionRevealedAt is null ? MessageRenderer.SolutionKeyboard(task.Id) : null;
        await messenger.SendHtmlAsync(settings.ChatId, reviewMessages, keyboard, cancellationToken);
    }

    public async Task RevealSolutionAsync(long chatId, PracticeTask task, CancellationToken cancellationToken)
    {
        task.SolutionRevealedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await messenger.SendHtmlAsync(chatId, MessageRenderer.Solution(task), null, cancellationToken);
    }

    public Task<PracticeTask?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        db.PracticeTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

    /// <summary>/next: ещё одна задача по теме последнего набора.</summary>
    public async Task<PracticeTask?> SendNextTaskAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        var topicId = await db.Digests
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => d.TopicId)
            .FirstOrDefaultAsync(cancellationToken);

        var topic = topicId is null ? null : await db.Topics.FirstOrDefaultAsync(t => t.Id == topicId, cancellationToken);
        if (topic is null)
        {
            return null;
        }

        await messenger.TypingAsync(settings.ChatId, cancellationToken);

        var previous = await PreviousTitlesAsync(db, topic.Id, cancellationToken);
        var generated = await generator.GenerateTasksAsync(
            new TopicContext(topic.Title, topic.ModuleTitle),
            [new TaskRequest(NaturalLanguage(topic), $"ещё одна задача по теме «{topic.Title}», другой аспект темы")],
            previous,
            cancellationToken);

        var task = ToEntity(generated[0], topic.Id, digestId: null, order: 1, clock.UtcNow);
        db.PracticeTasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);

        task.TelegramMessageId = await messenger.SendHtmlAsync(
            settings.ChatId, MessageRenderer.Task(task, 1, "Ещё задача"), null, cancellationToken);
        task.SentAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return task;
    }

    internal static string NaturalLanguage(Topic topic) => topic.Id.StartsWith("sql.", StringComparison.Ordinal) ? "sql" : "csharp";

    internal static async Task<IReadOnlyList<string>> PreviousTitlesAsync(BotDbContext db, string topicId, CancellationToken cancellationToken) =>
        await db.PracticeTasks
            .Where(t => t.TopicId == topicId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => t.Title)
            .Take(20)
            .ToListAsync(cancellationToken);

    internal static PracticeTask ToEntity(GeneratedTask task, string topicId, Guid? digestId, int order, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        DigestId = digestId,
        TopicId = topicId,
        OrderInDigest = order,
        Language = task.Language == "sql" ? TaskLanguage.Sql : TaskLanguage.CSharp,
        Title = task.Title,
        Statement = task.Statement,
        ExpectedQuestions = task.ExpectedQuestions.ToList(),
        ReferenceSolution = task.ReferenceSolution,
        SolutionExplanation = task.SolutionExplanation,
        Complexity = task.Complexity,
        CreatedAt = now,
    };
}
