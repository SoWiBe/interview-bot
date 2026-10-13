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
using Microsoft.Extensions.Options;
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
    TaskSender taskSender,
    DigestGenerationGate gate,
    BotClock clock,
    IOptions<GitHubOptions> gitHubOptions,
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

        var early = new List<DigestPart>();
        var digest = await BuildAsync(
            DigestKind.Daily, today, plan, topic: null,
            async ready => early.AddRange(await DeliverEarlyAsync(settings.ChatId, ready, cancellationToken)),
            cancellationToken);

        var delivery = await DeliverAsync(settings.ChatId, digest, cancellationToken);
        // теория могла уйти раньше задач — для вызывающего это одна доставка
        delivery = new DeliveryResult([.. early, .. delivery.Sent], delivery.AlreadySent.Except(early).ToList());

        logger.LogInformation(
            "Daily digest {DigestId} for {Date}: sent {Sent}, already sent {AlreadySent}",
            digest.Id, today, delivery.Sent, delivery.AlreadySent);

        return new DigestRunResult(plan, digest, delivery);
    }

    /// <summary>
    /// Готовит утреннюю сессию заранее, ничего не отправляя: генерация занимает минуты,
    /// а ко времени отправки набор уже лежит в БД и уходит сразу.
    /// </summary>
    /// <param name="lead">За сколько до времени отправки идёт подготовка — «сегодня» считается на момент отправки.</param>
    public async Task PrepareDailyAsync(UserSettings settings, TimeSpan lead, CancellationToken cancellationToken)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow.Add(lead), zone).DateTime);
        var plan = DailyPlanner.Plan(day, settings.WeekendMode, settings.PausedUntil);
        if (plan is DailyPlan.SkipPaused or DailyPlan.SkipWeekend)
        {
            return;
        }

        var digest = await BuildAsync(DigestKind.Daily, day, plan, topic: null, onTheoryReady: null, cancellationToken);
        logger.LogInformation("Daily digest {DigestId} for {Date} is prepared", digest.Id, day);
    }

    public Task<bool> HasDailyAsync(DateOnly day, CancellationToken cancellationToken) =>
        db.Digests.AnyAsync(d => d.Kind == DigestKind.Daily && d.ForDate == day, cancellationToken);

    /// <summary>Следующая тема из обычной очереди (пробелы → повторения → новые), кроме показанных сегодня и вчера.</summary>
    public Task<Topic> SelectNextTopicAsync(UserSettings settings, CancellationToken cancellationToken) =>
        SelectTopicAsync(DailyPlan.Full, clock.Today(settings.TimeZoneId), cancellationToken);

    /// <summary>/topic и «Новая тема»: теория и одна задача по теме, без влияния на утреннюю сессию.</summary>
    public async Task<Digest> RunManualAsync(UserSettings settings, Topic topic, CancellationToken cancellationToken)
    {
        var digest = await BuildAsync(
            DigestKind.Manual, clock.Today(settings.TimeZoneId), DailyPlan.Full, topic,
            ready => DeliverEarlyAsync(settings.ChatId, ready, cancellationToken),
            cancellationToken);

        await DeliverAsync(settings.ChatId, digest, cancellationToken);
        return digest;
    }

    /// <summary>Отдаёт теорию, не дожидаясь задач, и предупреждает, что задачи ещё в работе.</summary>
    private async Task<IReadOnlyList<DigestPart>> DeliverEarlyAsync(long chatId, Digest digest, CancellationToken cancellationToken)
    {
        var delivery = await DeliverAsync(chatId, digest, cancellationToken);
        if (delivery.Sent.Count > 0)
        {
            await messenger.SendTextAsync(chatId, "⏳ Задачи ещё готовлю — пришлю первую, как только будет готова.", cancellationToken);
        }

        return delivery.Sent;
    }

    /// <summary>
    /// Возвращает набор с задачами: существующий, заново сгенерированный или догенерированный.
    /// Генерация идёт в два шага. Теория готова за секунды, задачи — за минуты, поэтому набор сохраняется
    /// сразу после теории (<paramref name="onTheoryReady"/> может её отправить), а задачи дописываются в него позже.
    /// Если процесс упал между шагами, следующий вызов найдёт набор без задач и сгенерирует только их.
    /// </summary>
    private async Task<Digest> BuildAsync(
        DigestKind kind, DateOnly day, DailyPlan plan, Topic? topic, Func<Digest, Task>? onTheoryReady, CancellationToken cancellationToken)
    {
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var digest = kind == DigestKind.Daily ? await LoadDailyAsync(day, cancellationToken) : null;
            Task<List<PracticeTask>>? tasksInFlight = null;

            if (digest is null)
            {
                topic ??= await SelectTopicAsync(plan, day, cancellationToken);
                var context = new TopicContext(topic.Title, topic.ModuleTitle);
                var withTheory = plan != DailyPlan.ReviewTask;
                digest = new Digest
                {
                    Id = Guid.NewGuid(),
                    Kind = kind,
                    ForDate = day,
                    TopicId = topic.Id,
                    CreatedAt = clock.UtcNow,
                };

                // задачи — самое долгое: запускаем их сразу и не ждём, пока готовится и уходит теория
                tasksInFlight = await StartTaskGenerationAsync(digest.Id, kind, withTheory, topic, day, cancellationToken);

                if (withTheory)
                {
                    var theoryTask = generator.GenerateTheoryAsync(context, cancellationToken);
                    var snippetTask = gitHubOptions.Value.SnippetEnabled
                        ? snippets.FindAsync(context, topic.CodeRefs, cancellationToken)
                        : Task.FromResult<CodeSnippet?>(null);
                    await Task.WhenAll(theoryTask, snippetTask);

                    digest.TheoryHtml = MessageRenderer.Theory(topic, await theoryTask);
                    if (await snippetTask is { } snippet)
                    {
                        digest.SnippetHtml = MessageRenderer.Snippet(snippet);
                        digest.SnippetPermalink = snippet.Permalink;
                    }
                }

                if (topic.Progress is not null)
                {
                    topic.Progress.LastShownOn = day;
                }

                db.Digests.Add(digest);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    // другой экземпляр сервиса успел раньше — используем его набор, свой выбрасываем; задачи он допишет сам
                    logger.LogWarning("Daily digest for {Date} was created concurrently, using existing one", day);
                    db.ChangeTracker.Clear();
                    return await LoadDailyAsync(day, cancellationToken)
                        ?? throw new InvalidOperationException("Digest disappeared after unique violation");
                }

                if (onTheoryReady is not null && !tasksInFlight.IsCompleted)
                {
                    await onTheoryReady(digest);
                }
            }

            if (digest.Tasks.Count == 0)
            {
                if (tasksInFlight is null)
                {
                    logger.LogInformation("Digest {DigestId} has no tasks yet, generating them", digest.Id);
                    topic = await db.Topics.Include(t => t.Progress).FirstAsync(t => t.Id == digest.TopicId, cancellationToken);
                    tasksInFlight = await StartTaskGenerationAsync(
                        digest.Id, digest.Kind, withTheory: digest.TheoryHtml is not null, topic, digest.ForDate, cancellationToken);
                }

                // AddRange, а не digest.Tasks.Add: набор уже отслеживается, и задачи с готовым Id EF принял бы за существующие
                db.PracticeTasks.AddRange(await tasksInFlight);
                await db.SaveChangesAsync(cancellationToken);
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

    /// <summary>
    /// Читает из БД всё нужное для задач и запускает генерацию. Возвращённая задача с БД уже не работает,
    /// поэтому может идти параллельно с остальной работой над набором (DbContext не потокобезопасен).
    /// </summary>
    private async Task<Task<List<PracticeTask>>> StartTaskGenerationAsync(
        Guid digestId, DigestKind kind, bool withTheory, Topic topic, DateOnly day, CancellationToken cancellationToken)
    {
        var previous = await PracticeService.PreviousTitlesAsync(db, cancellationToken);
        var category = await PracticeService.NextCategoryAsync(db, cancellationToken);

        // утренняя сессия — три задачи; внеочередная тема — одна задача по ней; набор без теории — задача-повторение
        IReadOnlyList<TaskRequest> requests = (withTheory, kind) switch
        {
            (false, _) => [TopicTaskRequest(topic, category, $"задача-повторение по теме «{topic.Title}», чтобы проверить, не забыл ли")],
            (true, DigestKind.Daily) => SessionTaskRequests(topic, day, category),
            _ => [TopicTaskRequest(topic, category, $"задача по теме «{topic.Title}»")],
        };

        var generation = GenerateTasksAsync(digestId, topic, requests, previous, category, cancellationToken);
        // если теория упадёт раньше, результат задач никто не дождётся — ошибка не должна остаться ненаблюдённой
        _ = generation.ContinueWith(
            t => logger.LogWarning(t.Exception, "Task generation for digest {DigestId} failed", digestId),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return generation;
    }

    private async Task<List<PracticeTask>> GenerateTasksAsync(
        Guid digestId, Topic topic, IReadOnlyList<TaskRequest> requests, IReadOnlyList<string> previous, CSharpCategory category,
        CancellationToken cancellationToken)
    {
        var context = new TopicContext(topic.Title, topic.ModuleTitle);

        // Отдельный запрос на каждый язык: один запрос «всё сразу» генерируется дольше таймаута,
        // а SQL-задачи должны быть вместе из-за общей схемы.
        var groups = requests.GroupBy(r => r.Language).Select(g => g.ToList()).ToList();
        var batches = await Task.WhenAll(groups.Select(g => generator.GenerateTasksAsync(context, g, previous, cancellationToken)));

        // запросы сессии идут подряд по языкам (SQL, SQL, C#), поэтому склейка пачек сохраняет их порядок.
        // Роль и язык берём из своего запроса, а не из ответа модели: по ним строится сессия
        var tasks = batches
            .SelectMany((batch, i) => batch.Take(groups[i].Count))
            .Select((t, i) => PracticeService.ToEntity(
                t with { Language = requests[i].Language },
                topic.Id, digestId, i + 1, requests[i].Warmup ? TaskRole.Warmup : TaskRole.Main, category, clock.UtcNow))
            .ToList();

        // Обе SQL-задачи сессии — на одной схеме: во второй она показывается свёрнутой как «та же»
        var sqlTasks = tasks.Where(t => t.Language == TaskLanguage.Sql && t.Schema is not null).ToList();
        foreach (var task in sqlTasks.Skip(1))
        {
            task.Schema = sqlTasks[0].Schema;
        }

        return tasks;
    }

    /// <summary>
    /// Утренняя сессия всегда одной формы: SQL-разогрев, основная SQL-задача на той же схеме, задача на C#.
    /// Тема дня достаётся той задаче, чей язык ей подходит; остальные идут по кругу.
    /// </summary>
    private static IReadOnlyList<TaskRequest> SessionTaskRequests(Topic topic, DateOnly today, CSharpCategory category)
    {
        var isSqlTopic = PracticeService.NaturalLanguage(topic) == "sql";

        return
        [
            new TaskRequest("sql", "одна простая конструкция: фильтр с сортировкой, простой JOIN или GROUP BY с одним агрегатом", Warmup: true),
            new TaskRequest("sql", (isSqlTopic ? $"по теме дня «{topic.Title}»" : SqlFocus(today)) + ". На той же схеме, что и разогрев"),
            new TaskRequest("csharp", CategoryFocus(category) +
                (isSqlTopic ? " К теме дня не привязывайся." : $" По теме дня «{topic.Title}», если она ложится в этот формат; иначе выбери ближайший подходящий.")),
        ];
    }

    private static TaskRequest TopicTaskRequest(Topic topic, CSharpCategory category, string focus) =>
        PracticeService.NaturalLanguage(topic) == "sql"
            ? new TaskRequest("sql", focus)
            : new TaskRequest("csharp", $"{focus}. Желательный формат, если тема в него ложится: {CategoryFocus(category)}");

    /// <summary>Темы основной SQL-задачи, по кругу.</summary>
    internal static string SqlFocus(DateOnly day) => (day.DayNumber % 4) switch
    {
        0 => "GROUP BY и HAVING: отбор групп по агрегату",
        1 => "JOIN, который размножает строки: нужен COUNT(DISTINCT) или агрегация до JOIN",
        2 => "оконная функция: ROW_NUMBER, RANK, LAG или сумма нарастающим итогом",
        _ => "топ-N в каждой группе",
    };

    internal static string CategoryFocus(CSharpCategory category) => category switch
    {
        CSharpCategory.Algorithms => "алгоритм: бинарный поиск по отсортированным данным, два указателя, скользящее окно или хеш-таблица.",
        CSharpCategory.Concurrency => "потоки и async: отмена, таймаут через linked CTS, ограничение параллелизма или блокировка в async-коде.",
        CSharpCategory.WriteCode => "написать небольшой участок кода: retry с backoff, кэш с TTL, rate limiter, батчинг или метод-расширение.",
        CSharpCategory.FixBug => "найти и починить баг: в statement дай код на 15–25 строк с одной содержательной ошибкой, нужно найти её и исправить.",
        CSharpCategory.Collections => "коллекции и LINQ: выбор структуры данных, запрос LINQ или отложенное выполнение.",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>Заголовок задачи в наборе без теории (выходная задача-повторение).</summary>
    internal static string? TaskHeader(Digest digest) =>
        digest is { Kind: DigestKind.Daily, TheoryHtml: null } ? MessageRenderer.ReviewTaskHeader : null;

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
            new TelegramDigestPartSender(messenger, taskSender, digest, chatId));

        return await delivery.DeliverAsync(digest.Id, parts, cancellationToken);
    }
}
