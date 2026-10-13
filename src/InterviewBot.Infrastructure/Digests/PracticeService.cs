using InterviewBot.Core.Schedule;
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
    TaskSender taskSender,
    ProgressService progress,
    BotClock clock)
{
    /// <summary>Сколько последних реплик переписки отдаём модели.</summary>
    private const int HistoryLimit = 30;

    /// <summary>
    /// Текст без команды. Оценивается только то, что пользователь сам назвал решением (нажал «Отправить решение»);
    /// всё остальное — разговор с интервьюером.
    /// </summary>
    public async Task HandleTextAsync(
        UserSettings settings, string text, int userMessageId, int? replyToMessageId, CancellationToken cancellationToken)
    {
        var replied = await FindTaskByMessageAsync(replyToMessageId, cancellationToken);
        var awaiting = await db.PracticeTasks
            .Where(t => t.AwaitingSolutionSince != null)
            .OrderByDescending(t => t.AwaitingSolutionSince)
            .FirstOrDefaultAsync(cancellationToken);

        // reply на другую задачу — явный вопрос по ней, ожидание решения не трогаем
        if (awaiting is not null && (replied is null || replied.Id == awaiting.Id))
        {
            await ReviewAttemptAsync(settings, awaiting, text, userMessageId, cancellationToken);
            return;
        }

        var task = replied ?? await FindCurrentTaskAsync(cancellationToken);
        if (task is null)
        {
            await messenger.SendTextAsync(settings.ChatId, "Сейчас нет открытой задачи. Начать сессию — /today, взять задачу — /next", cancellationToken);
            return;
        }

        await ReplyInDialogueAsync(settings, task, text, userMessageId, cancellationToken);
    }

    /// <summary>Задача, к которой относится reply: на её условие или на любое сообщение переписки по ней.</summary>
    public async Task<PracticeTask?> FindTaskByMessageAsync(int? replyToMessageId, CancellationToken cancellationToken)
    {
        if (replyToMessageId is not { } messageId)
        {
            return null;
        }

        return await db.PracticeTasks.FirstOrDefaultAsync(t => t.TelegramMessageId == messageId, cancellationToken)
            ?? await db.PracticeTasks.FirstOrDefaultAsync(
                t => t.Messages.Any(m => m.TelegramMessageId == messageId), cancellationToken);
    }

    /// <summary>Текущая задача: последняя отправленная и ещё не закрытая.</summary>
    public Task<PracticeTask?> FindCurrentTaskAsync(CancellationToken cancellationToken) =>
        db.PracticeTasks
            .Where(t => t.SentAt != null && t.ClosedAt == null)
            .OrderByDescending(t => t.SentAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<PracticeTask?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        db.PracticeTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

    /// <summary>«Отправить решение»: следующее сообщение пользователя будет решением этой задачи.</summary>
    public async Task StartAwaitingSolutionAsync(long chatId, PracticeTask task, CancellationToken cancellationToken)
    {
        await db.PracticeTasks
            .Where(t => t.AwaitingSolutionSince != null && t.Id != task.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AwaitingSolutionSince, (DateTimeOffset?)null), cancellationToken);

        task.AwaitingSolutionSince = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await messenger.SendHtmlAsync(
            chatId, [MessageRenderer.AwaitingSolution(task)], MessageRenderer.AwaitingKeyboard(task.Id), cancellationToken);
    }

    public Task CancelAwaitingSolutionAsync(Guid taskId, CancellationToken cancellationToken) =>
        db.PracticeTasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AwaitingSolutionSince, (DateTimeOffset?)null), cancellationToken);

    private async Task ReplyInDialogueAsync(
        UserSettings settings, PracticeTask task, string text, int userMessageId, CancellationToken cancellationToken)
    {
        await messenger.TypingAsync(settings.ChatId, cancellationToken);

        var history = await LoadHistoryAsync(task.Id, cancellationToken);
        var reply = await generator.ReplyInDialogueAsync(ToView(task), history, text, cancellationToken);

        var question = NewMessage(task.Id, TaskMessageKind.Question, text, userMessageId);
        db.TaskMessages.Add(question);

        int botMessageId;
        string botText;
        if (reply.LooksLikeSolution)
        {
            // не оцениваем без явного согласия, но и не заставляем пересылать текст заново
            botText = MessageRenderer.LooksLikeSolution;
            botMessageId = await messenger.SendHtmlAsync(
                settings.ChatId, [TelegramHtml.Escape(botText)], MessageRenderer.CheckAsSolutionKeyboard(question.Id), cancellationToken);
        }
        else
        {
            botText = reply.Reply;
            botMessageId = await messenger.SendHtmlAsync(settings.ChatId, MessageRenderer.DialogueAnswer(botText), null, cancellationToken);
        }

        db.TaskMessages.Add(NewMessage(task.Id, TaskMessageKind.Answer, botText, botMessageId));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task GiveHintAsync(UserSettings settings, PracticeTask task, CancellationToken cancellationToken)
    {
        await messenger.TypingAsync(settings.ChatId, cancellationToken);

        var hintNumber = await CountHintsAsync(task.Id, cancellationToken) + 1;
        var history = await LoadHistoryAsync(task.Id, cancellationToken);
        var hint = await generator.GiveHintAsync(ToView(task), history, hintNumber, cancellationToken);

        var messageId = await messenger.SendHtmlAsync(settings.ChatId, MessageRenderer.HintMessage(hintNumber, hint), null, cancellationToken);
        db.TaskMessages.Add(NewMessage(task.Id, TaskMessageKind.Hint, hint, messageId));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>«Проверить это как решение»: сдаёт сообщение, уже отправленное в диалоге.</summary>
    /// <returns>false — сообщение не найдено или уже сдано.</returns>
    public async Task<bool> SubmitMessageAsSolutionAsync(UserSettings settings, Guid messageId, CancellationToken cancellationToken)
    {
        var message = await db.TaskMessages.FirstOrDefaultAsync(
            m => m.Id == messageId && m.Kind == TaskMessageKind.Question, cancellationToken);
        var task = message is null ? null : await GetTaskAsync(message.TaskId, cancellationToken);
        if (message is null || task is null)
        {
            return false;
        }

        await ReviewAttemptAsync(settings, task, message.Text, message.TelegramMessageId, cancellationToken, message);
        return true;
    }

    /// <param name="existing">Сообщение из диалога, которое сдаётся как решение: в истории оно станет решением, а не вопросом.</param>
    private async Task ReviewAttemptAsync(
        UserSettings settings, PracticeTask task, string answer, int? userMessageId, CancellationToken cancellationToken,
        TaskMessage? existing = null)
    {
        await messenger.SendTextAsync(settings.ChatId, $"🔎 Смотрю решение задачи «{task.Title}»…", cancellationToken);
        await messenger.TypingAsync(settings.ChatId, cancellationToken);

        var history = await LoadHistoryAsync(task.Id, cancellationToken, exceptMessageId: existing?.Id);
        var hintsUsed = await CountHintsAsync(task.Id, cancellationToken);
        var review = await generator.ReviewAttemptAsync(ToView(task), history, hintsUsed, answer, cancellationToken);
        var reviewMessages = MessageRenderer.Review(review, task, hintsUsed);

        db.TaskAttempts.Add(new TaskAttempt
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            SubmittedAt = clock.UtcNow,
            Answer = answer,
            AskedClarifyingQuestions = review.AskedClarifyingQuestions,
            Score = Math.Clamp(review.Score, 1, 5),
            HintsUsed = hintsUsed,
            ReviewHtml = string.Join(TelegramHtml.BlockSeparator, reviewMessages),
        });

        // сбрасываем только после успешного ревью: если модель упала, решение можно прислать ещё раз без кнопки
        task.AwaitingSolutionSince = null;

        if (existing is not null)
        {
            existing.Kind = TaskMessageKind.Solution;
        }
        else
        {
            db.TaskMessages.Add(NewMessage(task.Id, TaskMessageKind.Solution, answer, userMessageId));
        }

        await db.SaveChangesAsync(cancellationToken);
        await progress.MarkActiveAsync(clock.Today(settings.TimeZoneId), cancellationToken);

        var keyboard = MessageRenderer.NextStepsKeyboard(
            await NextStepForAsync(task, cancellationToken),
            task.Id,
            retryTaskId: task.Id,
            solutionTaskId: task.SolutionRevealedAt is null ? task.Id : null);
        var reviewMessageId = await messenger.SendHtmlAsync(settings.ChatId, reviewMessages, keyboard, cancellationToken);

        db.TaskMessages.Add(NewMessage(task.Id, TaskMessageKind.Review, MessageRenderer.ReviewPlainText(review), reviewMessageId));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevealSolutionAsync(long chatId, PracticeTask task, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        task.SolutionRevealedAt ??= now;
        task.AwaitingSolutionSince = null;
        await db.SaveChangesAsync(cancellationToken);

        var messages = MessageRenderer.Solution(task).ToList();

        // Набор без теории (выходная задача) — кнопок оценки темы ещё не было: даём их здесь
        Guid? feedbackDigestId = null;
        if (task.DigestId is { } digestId
            && await db.Digests.AnyAsync(d => d.Id == digestId && d.TheoryHtml == null && d.Feedback == null, cancellationToken))
        {
            feedbackDigestId = digestId;
            messages.Add(MessageRenderer.FeedbackPrompt);
        }

        var keyboard = MessageRenderer.NextStepsKeyboard(
            await NextStepForAsync(task, cancellationToken), task.Id, feedbackDigestId: feedbackDigestId);
        await messenger.SendHtmlAsync(chatId, messages, keyboard, cancellationToken);
    }

    /// <summary>«Следующая задача» / «Завершить сессию»: закрывает задачу и делает следующий шаг сессии.</summary>
    public async Task AdvanceAsync(UserSettings settings, PracticeTask task, CancellationToken cancellationToken)
    {
        task.ClosedAt ??= clock.UtcNow;
        task.AwaitingSolutionSince = null;
        await db.SaveChangesAsync(cancellationToken);

        if (task.DigestId is { } digestId)
        {
            await ContinueSessionAsync(settings, digestId, announceWaiting: false, cancellationToken);
        }
    }

    /// <summary>
    /// Следующий шаг сессии по её состоянию в БД. Вызывается и кнопкой «дальше», и повторным /today —
    /// поэтому после сбоя отправки или рестарта сессия продолжается с того же места.
    /// </summary>
    /// <param name="announceWaiting">Напомнить, какая задача сейчас открыта (для /today).</param>
    public async Task ContinueSessionAsync(UserSettings settings, Guid digestId, bool announceWaiting, CancellationToken cancellationToken)
    {
        var digest = await db.Digests.Include(d => d.Tasks).FirstAsync(d => d.Id == digestId, cancellationToken);
        var step = SessionFlow.Next(digest.Tasks.Select(t => new SessionTask(t.OrderInDigest, t.SentAt != null, t.ClosedAt != null)));
        var current = digest.Tasks.FirstOrDefault(t => t.OrderInDigest == step.Order);

        switch (step.Kind)
        {
            case SessionStepKind.SendTask:
                await taskSender.SendAsync(settings.ChatId, current!, digest.Tasks, DigestService.TaskHeader(digest), cancellationToken);
                break;

            case SessionStepKind.WaitForCurrent when announceWaiting:
                await messenger.SendTextAsync(
                    settings.ChatId,
                    $"Сессия идёт: сейчас задача {current!.OrderInDigest} из {digest.Tasks.Count} — «{current.Title}». " +
                    "Пиши вопросы обычным сообщением, а кнопки — под задачей выше.",
                    cancellationToken);
                break;

            case SessionStepKind.Finished when digest.Tasks.Count > 1 && !announceWaiting:
                await SendSummaryAsync(settings, digest, cancellationToken);
                break;

            case SessionStepKind.Finished when announceWaiting:
                await messenger.SendTextAsync(settings.ChatId, "Сессия на сегодня пройдена ✅ Хочешь ещё задачу — /next", cancellationToken);
                break;
        }
    }

    private async Task SendSummaryAsync(UserSettings settings, Digest digest, CancellationToken cancellationToken)
    {
        var taskIds = digest.Tasks.Select(t => t.Id).ToList();
        var attempts = await db.TaskAttempts
            .Where(a => taskIds.Contains(a.TaskId))
            .Select(a => new { a.TaskId, a.Score, a.AskedClarifyingQuestions })
            .ToListAsync(cancellationToken);

        var lines = digest.Tasks
            .OrderBy(t => t.OrderInDigest)
            .Select(t =>
            {
                var own = attempts.Where(a => a.TaskId == t.Id).ToList();
                return new SessionSummaryLine(t, own.Count > 0 ? own.Max(a => a.Score) : null, own.Any(a => a.AskedClarifyingQuestions));
            })
            .ToList();

        var stats = await progress.GetStatsAsync(clock.Today(settings.TimeZoneId), cancellationToken);
        await messenger.SendHtmlAsync(
            settings.ChatId, MessageRenderer.SessionSummary(lines, stats.Streak), MessageRenderer.FreeKeyboard, cancellationToken);
    }

    private async Task<NextStep> NextStepForAsync(PracticeTask task, CancellationToken cancellationToken)
    {
        if (task.DigestId is not { } digestId)
        {
            return NextStep.Free;
        }

        var others = await db.PracticeTasks
            .Where(t => t.DigestId == digestId && t.Id != task.Id)
            .Select(t => new { t.ClosedAt })
            .ToListAsync(cancellationToken);

        if (others.Count == 0)
        {
            return NextStep.Free;
        }

        return others.Any(t => t.ClosedAt == null) ? NextStep.NextTask : NextStep.FinishSession;
    }

    /// <summary>/next: дополнительная задача вне сессии на выбранном языке.</summary>
    public async Task<PracticeTask?> SendExtraTaskAsync(UserSettings settings, TaskLanguage language, CancellationToken cancellationToken)
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

        var category = language == TaskLanguage.CSharp ? await NextCategoryAsync(db, cancellationToken) : (CSharpCategory?)null;
        var onTopic = NaturalLanguage(topic) == LanguageCode(language);
        var focus = language == TaskLanguage.Sql
            ? (onTopic ? $"другой аспект темы «{topic.Title}»" : DigestService.SqlFocus(clock.Today(settings.TimeZoneId)) + ". К теме не привязывайся")
            : DigestService.CategoryFocus(category!.Value) + (onTopic ? $" По теме «{topic.Title}», если она ложится в этот формат." : " К теме не привязывайся.");

        var generated = await generator.GenerateTasksAsync(
            new TopicContext(topic.Title, topic.ModuleTitle),
            [new TaskRequest(LanguageCode(language), focus)],
            await PreviousTitlesAsync(db, cancellationToken),
            cancellationToken);

        var task = ToEntity(generated[0], topic.Id, digestId: null, order: 1, TaskRole.Main, category, clock.UtcNow);
        db.PracticeTasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);

        await taskSender.SendAsync(settings.ChatId, task, [task], MessageRenderer.ExtraTaskHeader, cancellationToken);
        return task;
    }

    private async Task<IReadOnlyList<DialogueTurn>> LoadHistoryAsync(Guid taskId, CancellationToken cancellationToken, Guid? exceptMessageId = null)
    {
        var messages = await db.TaskMessages
            .Where(m => m.TaskId == taskId && m.Id != exceptMessageId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(HistoryLimit)
            .ToListAsync(cancellationToken);

        return messages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new DialogueTurn(
                m.Kind is TaskMessageKind.Question or TaskMessageKind.Solution,
                m.Kind == TaskMessageKind.Hint ? "[Подсказка] " + m.Text : m.Text))
            .ToList();
    }

    private Task<int> CountHintsAsync(Guid taskId, CancellationToken cancellationToken) =>
        db.TaskMessages.CountAsync(m => m.TaskId == taskId && m.Kind == TaskMessageKind.Hint, cancellationToken);

    private TaskMessage NewMessage(Guid taskId, TaskMessageKind kind, string text, int? telegramMessageId) => new()
    {
        Id = Guid.NewGuid(),
        TaskId = taskId,
        Kind = kind,
        Text = text,
        TelegramMessageId = telegramMessageId,
        CreatedAt = clock.UtcNow,
    };

    private static PracticeTaskView ToView(PracticeTask task) => new(
        LanguageCode(task.Language), task.Statement, task.Schema ?? "", task.ExpectedQuestions, task.Clarifications, task.ReferenceSolution);

    internal static string LanguageCode(TaskLanguage language) => language == TaskLanguage.Sql ? "sql" : "csharp";

    internal static string NaturalLanguage(Topic topic) => topic.Id.StartsWith("sql.", StringComparison.Ordinal) ? "sql" : "csharp";

    /// <summary>Категория C#-задачи, которая не выпадала дольше всех.</summary>
    internal static async Task<CSharpCategory> NextCategoryAsync(BotDbContext db, CancellationToken cancellationToken)
    {
        var recent = await db.PracticeTasks
            .Where(t => t.Category != null)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => t.Category!.Value)
            .Take(20)
            .ToListAsync(cancellationToken);

        return TaskRotation.NextCategory(recent);
    }

    internal static async Task<IReadOnlyList<string>> PreviousTitlesAsync(BotDbContext db, CancellationToken cancellationToken) =>
        await db.PracticeTasks
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => t.Title)
            .Take(30)
            .ToListAsync(cancellationToken);

    internal static PracticeTask ToEntity(
        GeneratedTask task, string topicId, Guid? digestId, int order, TaskRole role, CSharpCategory? category, DateTimeOffset now)
    {
        var language = task.Language == "sql" ? TaskLanguage.Sql : TaskLanguage.CSharp;
        return new PracticeTask
        {
            Id = Guid.NewGuid(),
            DigestId = digestId,
            TopicId = topicId,
            OrderInDigest = order,
            Language = language,
            Role = role,
            Category = language == TaskLanguage.CSharp ? category : null,
            Title = task.Title,
            Statement = task.Statement,
            Schema = string.IsNullOrWhiteSpace(task.Schema) ? null : task.Schema,
            ExpectedQuestions = (task.ExpectedQuestions ?? []).ToList(),
            Clarifications = (task.Clarifications ?? []).ToList(),
            ReferenceSolution = task.ReferenceSolution,
            SolutionExplanation = task.SolutionExplanation,
            Complexity = task.Complexity,
            CreatedAt = now,
        };
    }
}
