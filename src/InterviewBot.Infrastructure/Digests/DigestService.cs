using InterviewBot.Core.Delivery;
using InterviewBot.Core.Schedule;
using InterviewBot.Core.Topics;
using InterviewBot.Infrastructure.Common;
using InterviewBot.Infrastructure.Content;
using InterviewBot.Infrastructure.GitHub;
using InterviewBot.Infrastructure.Persistence;
using InterviewBot.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace InterviewBot.Infrastructure.Digests;

/// <summary>
/// Не даёт двум генерациям (джоб и /today) идти одновременно внутри процесса.
/// Между процессами защищает уникальный индекс в БД (на этапе 2 — распределённый лок в Redis).
/// </summary>
public sealed class DigestGenerationGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed record DigestRunResult(DailyPlan Plan, Digest? Digest, DeliveryResult? Delivery)
{
    public bool NothingNew => Delivery is { Sent.Count: 0 };
}

public sealed class DigestService(
    BotDbContext db,
    IContentGenerator generator,
    CodeSnippetProvider snippets,
    BotMessenger messenger,
    DigestGenerationGate gate,
    BotClock clock,
    ILogger<DigestService> logger)
{
    /// <param name="manual">/today: пауза игнорируется, в «выходные выключены» присылаем полный набор.</param>
    public async Task<DigestRunResult> RunDailyAsync(UserSettings settings, bool manual, CancellationToken cancellationToken)
    {
        var today = clock.Today(settings.TimeZoneId);
        var plan = DailyPlanner.Plan(today, settings.WeekendMode, manual ? null : settings.PausedUntil);
        if (manual && plan == DailyPlan.SkipWeekend)
        {
            plan = DailyPlan.Full;
        }

        if (plan is DailyPlan.SkipPaused or DailyPlan.SkipWeekend)
        {
            logger.LogInformation("Daily digest for {Date} skipped: {Plan}", today, plan);
            return new DigestRunResult(plan, null, null);
        }

        var digest = await GetOrCreateDailyAsync(today, plan, cancellationToken);
        var delivery = await DeliverAsync(settings.ChatId, digest, cancellationToken);

        logger.LogInformation(
            "Daily digest {DigestId} for {Date}: sent {Sent}, already sent {AlreadySent}",
            digest.Id, today, delivery.Sent, delivery.AlreadySent);

        return new DigestRunResult(plan, digest, delivery);
    }

    public Task<bool> HasDailyAsync(DateOnly day, CancellationToken cancellationToken) =>
        db.Digests.AnyAsync(d => d.Kind == DigestKind.Daily && d.ForDate == day, cancellationToken);

    /// <summary>/topic: внеочередная тема, полный набор, без влияния на утренний.</summary>
    public async Task<Digest> RunManualAsync(UserSettings settings, Topic topic, CancellationToken cancellationToken)
    {
        var today = clock.Today(settings.TimeZoneId);
        var digest = await GenerateAsync(DigestKind.Manual, today, topic, DailyPlan.Full, cancellationToken);
        db.Digests.Add(digest);
        await db.SaveChangesAsync(cancellationToken);

        await DeliverAsync(settings.ChatId, digest, cancellationToken);
        return digest;
    }

    private async Task<Digest> GetOrCreateDailyAsync(DateOnly today, DailyPlan plan, CancellationToken cancellationToken)
    {
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var existing = await LoadDailyAsync(today, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            var topic = await SelectTopicAsync(plan, today, cancellationToken);
            var digest = await GenerateAsync(DigestKind.Daily, today, topic, plan, cancellationToken);
            db.Digests.Add(digest);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // другой экземпляр сервиса успел раньше — используем его набор, свой выбрасываем
                logger.LogWarning("Daily digest for {Date} was created concurrently, using existing one", today);
                db.ChangeTracker.Clear();
                return await LoadDailyAsync(today, cancellationToken)
                    ?? throw new InvalidOperationException("Digest disappeared after unique violation");
            }

            return digest;
        }
        finally
        {
            gate.Semaphore.Release();
        }
    }

    private Task<Digest?> LoadDailyAsync(DateOnly today, CancellationToken cancellationToken) =>
        db.Digests
            .Include(d => d.Tasks)
            .FirstOrDefaultAsync(d => d.Kind == DigestKind.Daily && d.ForDate == today, cancellationToken);

    private async Task<Topic> SelectTopicAsync(DailyPlan plan, DateOnly today, CancellationToken cancellationToken)
    {
        var topics = await db.Topics
            .Where(t => t.IsActive)
            .Include(t => t.Progress)
            .ToListAsync(cancellationToken);

        var states = topics
            .Where(t => t.Progress is not null)
            .Select(t => new TopicState(
                t.Id, t.Priority, t.SortOrder, t.Progress!.Status, t.Progress.Stage,
                t.Progress.NextReviewOn, t.Progress.LastShownOn, t.Progress.StatusChangedOn))
            .ToList();

        var topicId = plan == DailyPlan.ReviewTask
            ? TopicSelector.SelectForReview(states, today)
            : TopicSelector.SelectForTheory(states, today);

        return topics.FirstOrDefault(t => t.Id == topicId)
            ?? throw new InvalidOperationException("Curriculum is empty: no topic to select");
    }

    private async Task<Digest> GenerateAsync(DigestKind kind, DateOnly today, Topic topic, DailyPlan plan, CancellationToken cancellationToken)
    {
        var context = new TopicContext(topic.Title, topic.ModuleTitle);
        var digest = new Digest
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            ForDate = today,
            TopicId = topic.Id,
            CreatedAt = clock.UtcNow,
        };

        IReadOnlyList<GeneratedTask> tasks;
        if (plan == DailyPlan.ReviewTask)
        {
            var previous = await PracticeService.PreviousTitlesAsync(db, topic.Id, cancellationToken);
            tasks = await generator.GenerateTasksAsync(
                context,
                [new TaskRequest(PracticeService.NaturalLanguage(topic), $"задача-повторение по теме «{topic.Title}», чтобы проверить, не забыл ли")],
                previous,
                cancellationToken);
        }
        else
        {
            // Теория с задачами и поиск фрагмента кода независимы — запускаем параллельно
            var contentTask = generator.GenerateDailyAsync(context, DailyTaskRequests(topic, today), cancellationToken);
            var snippetTask = snippets.FindAsync(context, topic.CodeRefs, cancellationToken);
            await Task.WhenAll(contentTask, snippetTask);

            var content = await contentTask;
            var snippet = await snippetTask;

            digest.TheoryHtml = MessageRenderer.Theory(topic, content.Theory);
            if (snippet is not null)
            {
                digest.SnippetHtml = MessageRenderer.Snippet(snippet);
                digest.SnippetPermalink = snippet.Permalink;
            }

            tasks = content.Tasks;
        }

        digest.Tasks = tasks
            .Select((t, i) => PracticeService.ToEntity(t, topic.Id, digest.Id, i + 1, clock.UtcNow))
            .ToList();

        if (topic.Progress is not null)
        {
            topic.Progress.LastShownOn = today;
        }

        return digest;
    }

    /// <summary>
    /// Первая задача — по теме дня. Вторая чередуется: SQL-разминка через день, иначе алгоритм/async —
    /// прицельно по пробелам (сортированные данные, отмена, COUNT(DISTINCT), блокировки).
    /// </summary>
    private static IReadOnlyList<TaskRequest> DailyTaskRequests(Topic topic, DateOnly today)
    {
        var language = PracticeService.NaturalLanguage(topic);
        var first = new TaskRequest(language, $"задача по теме дня «{topic.Title}»");

        var second = language == "sql" || today.DayNumber % 2 == 1
            ? new TaskRequest("csharp",
                "разминка не по теме дня: алгоритм (бинарный поиск по отсортированным данным, два указателя, окно, хеш-таблица) " +
                "или async/многопоточность (отмена, таймаут через linked CTS, ограничение параллелизма, блокировки в async)")
            : new TaskRequest("sql",
                "SQL-разминка не по теме дня: GROUP BY/HAVING, COUNT(DISTINCT) при размножении строк в JOIN, " +
                "оконные функции или топ-N в группе");

        return [first, second];
    }

    private async Task<DeliveryResult> DeliverAsync(long chatId, Digest digest, CancellationToken cancellationToken)
    {
        var parts = new List<DigestPart>();
        if (digest.TheoryHtml is not null)
        {
            parts.Add(DigestPart.Theory);
        }

        if (digest.SnippetHtml is not null)
        {
            parts.Add(DigestPart.Snippet);
        }

        if (digest.Tasks.Count > 0)
        {
            parts.Add(DigestPart.Tasks);
        }

        var delivery = new DigestDelivery(
            new DbDeliveryClaimStore(db, clock),
            new TelegramDigestPartSender(db, messenger, digest, chatId, clock));

        return await delivery.DeliverAsync(digest.Id, parts, cancellationToken);
    }
}
