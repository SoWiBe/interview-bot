using System.Text;
using InterviewBot.Core.Schedule;
using InterviewBot.Core.Topics;
using InterviewBot.Infrastructure.Common;
using InterviewBot.Infrastructure.Digests;
using InterviewBot.Infrastructure.Persistence;
using InterviewBot.Infrastructure.Progress;
using InterviewBot.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using static InterviewBot.Infrastructure.Telegram.TelegramHtml;

namespace InterviewBot.Infrastructure.Telegram;

/// <summary>
/// Обрабатывает один апдейт от Telegram. Регистрируется как scoped: на каждый апдейт свой DbContext.
/// </summary>
public sealed class BotUpdateDispatcher(
    ITelegramBotClient bot,
    BotMessenger messenger,
    BotDbContext db,
    UserSettingsService settingsService,
    DigestService digests,
    PracticeService practice,
    ProgressService progress,
    DigestScheduler scheduler,
    BotClock clock,
    IOptions<TelegramOptions> options,
    ILogger<BotUpdateDispatcher> logger)
{
    private static readonly BotCommand[] Commands =
    [
        new("today", "Утренняя сессия сейчас"),
        new("next", "Ещё задача: SQL или C#"),
        new("topic", "Внеочередная тема: /topic <название>"),
        new("giveup", "Показать решение задачи"),
        new("stats", "Прогресс"),
        new("pause", "Пауза: /pause <дней>, /pause 0 — снять"),
        new("settings", "Время отправки и режим выходных"),
        new("help", "Что умеет бот"),
    ];

    private const string HelpText = """
        Каждое утро — одна сессия: короткая теория и три задачи по очереди.

        Как проходит сессия:
        0. Теория на 2 минуты. Отметь тему кнопкой: ✅ Понял (повторим через 3 → 7 → 21 день), 🔁 Повторить (завтра), ❌ Пробел (в начало очереди).
        1. SQL-разогрев, ~5 минут.
        2. Основная SQL-задача на той же схеме.
        3. Задача на C#: алгоритмы, потоки, «напиши код», «найди баг», коллекции — по кругу.
        Следующая задача приходит после кнопки «Следующая задача».

        Вопросы и решение:
        • Любое сообщение по задаче — это разговор: уточняй условие, обсуждай идею. Оценки за это нет.
        • Чтобы сдать — нажми «📤 Отправить решение» и пришли решение следующим сообщением. Только тогда будет ревью с оценкой.
        • «💡 Подсказка» — следующий шаг, «🏳 Показать решение» — эталон с разбором.
        В выходные по умолчанию одна задача на повторение (/settings weekend full — полная сессия).

        /today — сессия сейчас или напоминание, на каком ты шаге
        /next — ещё задача вне сессии: SQL или C#
        /topic <название> — теория и задача по выбранной теме
        /giveup — показать решение задачи (можно reply на задачу)
        /stats — прогресс
        /pause <дней> — пауза без потери серии, /pause 0 — снять
        /settings — время отправки и режим выходных
        """;

    public async Task HandleAsync(Update update, CancellationToken cancellationToken)
    {
        var sender = update.Message?.From ?? update.CallbackQuery?.From;
        if (sender is null || sender.Id != options.Value.AllowedUserId)
        {
            logger.LogWarning("Ignoring update {UpdateId} from unknown user {UserId}", update.Id, sender?.Id);
            return;
        }

        var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;

        try
        {
            if (update.CallbackQuery is { } callback)
            {
                await HandleCallbackAsync(callback, cancellationToken);
            }
            else if (update.Message is { Text: { } text } message)
            {
                await HandleMessageAsync(message, text.Trim(), cancellationToken);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && chatId is not null)
        {
            // пользователь не должен остаться без ответа: сообщаем об ошибке и не роняем обработку
            logger.LogError(ex, "Failed to handle update {UpdateId}", update.Id);
            await messenger.SendTextAsync(chatId.Value, "⚠️ Что-то пошло не так, подробности в логах. Попробуй ещё раз чуть позже.", cancellationToken);
        }
    }

    private async Task HandleMessageAsync(Message message, string text, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;

        if (text.StartsWith('/'))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0].Split('@')[0].ToLowerInvariant();
            var argument = parts.Length > 1 ? parts[1] : "";

            if (command == "/start")
            {
                await StartAsync(message, cancellationToken);
                return;
            }

            var settings = await settingsService.GetAsync(cancellationToken);
            if (settings is null)
            {
                await messenger.SendTextAsync(chatId, "Сначала /start", cancellationToken);
                return;
            }

            await (command switch
            {
                "/today" => TodayAsync(settings, cancellationToken),
                "/next" => NextAsync(settings, cancellationToken),
                "/topic" => TopicAsync(settings, argument, cancellationToken),
                "/giveup" => GiveUpAsync(settings, message.ReplyToMessage?.MessageId, cancellationToken),
                "/stats" => StatsAsync(settings, cancellationToken),
                "/pause" => PauseAsync(settings, argument, cancellationToken),
                "/settings" => SettingsAsync(settings, argument, cancellationToken),
                "/help" => messenger.SendTextAsync(chatId, HelpText, cancellationToken),
                _ => messenger.SendTextAsync(chatId, "Не знаю такой команды. /help", cancellationToken),
            });
            return;
        }

        var user = await settingsService.GetAsync(cancellationToken);
        if (user is null)
        {
            await messenger.SendTextAsync(chatId, "Сначала /start", cancellationToken);
            return;
        }

        // Текст без команды — разговор по задаче; решением он становится только после «Отправить решение»
        await practice.HandleTextAsync(user, text, message.MessageId, message.ReplyToMessage?.MessageId, cancellationToken);
    }

    private async Task StartAsync(Message message, CancellationToken cancellationToken)
    {
        var settings = await settingsService.RegisterAsync(message.From!.Id, message.Chat.Id, cancellationToken);
        await scheduler.EnsureScheduledAsync(settings, cancellationToken);

        logger.LogInformation("User {UserId} registered in chat {ChatId}", settings.TelegramUserId, settings.ChatId);
        await messenger.SendTextAsync(
            settings.ChatId,
            $"Привет! Я твой помощник для подготовки к собеседованиям.\n\n" +
            $"Сессия приходит каждый день в {settings.SendTime:HH\\:mm} ({settings.TimeZoneId}). Хочешь начать прямо сейчас — /today.\n\n{HelpText}",
            cancellationToken);

        // меню команд — удобство, а не обязательная часть регистрации: его сбой не должен ломать /start
        await TryRegisterCommandsAsync(bot, logger, cancellationToken);
    }

    /// <summary>Регистрирует меню команд в Telegram. Ошибку только логирует: команды работают и без меню.</summary>
    public static async Task<bool> TryRegisterCommandsAsync(ITelegramBotClient bot, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await bot.SetMyCommands(Commands, cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to register bot commands menu");
            return false;
        }
    }

    private async Task TodayAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        var today = clock.Today(settings.TimeZoneId);
        if (!await digests.HasDailyAsync(today, cancellationToken))
        {
            await messenger.SendTextAsync(settings.ChatId, "⏳ Готовлю сессию на сегодня. Теория придёт сразу, как будет готова, задачи — через несколько минут.", cancellationToken);
            await messenger.TypingAsync(settings.ChatId, cancellationToken);
        }

        var result = await digests.RunDailyAsync(settings, manual: true, cancellationToken);
        if (result is { NothingNew: true, Digest: { } digest })
        {
            // сессия уже идёт: напоминаем текущий шаг или досылаем задачу, которая не ушла из-за сбоя
            await practice.ContinueSessionAsync(settings, digest.Id, announceWaiting: true, cancellationToken);
        }
    }

    private Task NextAsync(UserSettings settings, CancellationToken cancellationToken) =>
        messenger.SendHtmlAsync(settings.ChatId, ["Какую задачу дать?"], MessageRenderer.ExtraTaskKeyboard, cancellationToken);

    private async Task ExtraTaskAsync(UserSettings settings, TaskLanguage language, CancellationToken cancellationToken)
    {
        await messenger.SendTextAsync(settings.ChatId, "⏳ Готовлю задачу…", cancellationToken);
        var task = await practice.SendExtraTaskAsync(settings, language, cancellationToken);
        if (task is null)
        {
            await messenger.SendTextAsync(settings.ChatId, "Пока нет текущей темы. Начни с /today или /topic", cancellationToken);
        }
    }

    /// <summary>«📚 Новая тема»: следующая тема из очереди — теория и задача по ней.</summary>
    private async Task NewTopicAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        var topic = await digests.SelectNextTopicAsync(settings, cancellationToken);
        await messenger.SendTextAsync(settings.ChatId, $"⏳ Готовлю тему «{topic.Title}». Теория придёт сразу, как будет готова, задача — чуть позже.", cancellationToken);
        await messenger.TypingAsync(settings.ChatId, cancellationToken);
        await digests.RunManualAsync(settings, topic, cancellationToken);
    }

    private async Task TopicAsync(UserSettings settings, string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            await messenger.SendTextAsync(settings.ChatId, "Напиши, какую тему: /topic SemaphoreSlim или /topic оконные функции", cancellationToken);
            return;
        }

        var pattern = $"%{query.Replace("%", "").Replace("_", "")}%";
        var matches = await db.Topics
            .Where(t => t.IsActive && (t.Id == query || EF.Functions.ILike(t.Title, pattern) || EF.Functions.ILike(t.Id, pattern)))
            .Include(t => t.Progress)
            .OrderBy(t => t.SortOrder)
            .Take(6)
            .ToListAsync(cancellationToken);

        var topic = matches.FirstOrDefault(t => t.Id == query) ?? (matches.Count == 1 ? matches[0] : null);
        if (topic is null)
        {
            var reply = matches.Count == 0
                ? "Не нашёл такую тему. Попробуй другое слово."
                : "Нашёл несколько тем, уточни:\n" + string.Join("\n", matches.Select(t => $"/topic {t.Id} — {t.Title}"));
            await messenger.SendTextAsync(settings.ChatId, reply, cancellationToken);
            return;
        }

        await messenger.SendTextAsync(settings.ChatId, $"⏳ Готовлю тему «{topic.Title}». Теория придёт сразу, как будет готова, задача — чуть позже.", cancellationToken);
        await messenger.TypingAsync(settings.ChatId, cancellationToken);
        await digests.RunManualAsync(settings, topic, cancellationToken);
    }

    private async Task GiveUpAsync(UserSettings settings, int? replyToMessageId, CancellationToken cancellationToken)
    {
        var task = await practice.FindTaskByMessageAsync(replyToMessageId, cancellationToken)
            ?? await practice.FindCurrentTaskAsync(cancellationToken);
        if (task is null)
        {
            await messenger.SendTextAsync(settings.ChatId, "Нет открытых задач. Ответь /giveup reply на задачу, если нужна конкретная.", cancellationToken);
            return;
        }

        await practice.RevealSolutionAsync(settings.ChatId, task, cancellationToken);
    }

    private async Task StatsAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        var today = clock.Today(settings.TimeZoneId);
        var stats = await progress.GetStatsAsync(today, cancellationToken);

        int Count(TopicStatus status) => stats.TopicsByStatus.GetValueOrDefault(status);

        var questionsShare = stats.AttemptedTasks == 0 ? "—" : $"{100.0 * stats.TasksWithQuestions / stats.AttemptedTasks:0}%";
        var html = new StringBuilder()
            .AppendLine($"📊 {Bold("Прогресс")}")
            .AppendLine()
            .AppendLine($"🔥 Серия: {Bold($"{stats.Streak} дн.")}")
            .AppendLine($"❓ Задал уточняющие вопросы: {Bold(questionsShare)} задач ({stats.TasksWithQuestions} из {stats.AttemptedTasks})")
            .AppendLine($"⭐ Средняя оценка: {(stats.AverageScore is { } avg ? $"{avg:0.0}/5" : "—")}")
            .AppendLine($"🏁 Сессий пройдено полностью: {Bold(stats.CompletedSessions.ToString())}")
            .AppendLine()
            .AppendLine(Bold("Темы"))
            .AppendLine($"❌ Пробел: {Count(TopicStatus.Gap)}")
            .AppendLine($"🔁 Повторить: {Count(TopicStatus.Repeat)}")
            .AppendLine($"✅ Понял: {Count(TopicStatus.Understood) - stats.Mastered} (на повторении), освоено: {stats.Mastered}")
            .AppendLine($"🆕 Новые: {Count(TopicStatus.New)}")
            .AppendLine($"📅 К повторению сегодня: {stats.DueToday}");

        if (settings.PausedUntil is { } until && until >= today)
        {
            html.AppendLine().AppendLine($"⏸ Пауза до {until:dd.MM} включительно");
        }

        await messenger.SendHtmlAsync(settings.ChatId, html.ToString(), null, cancellationToken);
    }

    private async Task PauseAsync(UserSettings settings, string argument, CancellationToken cancellationToken)
    {
        if (!int.TryParse(argument, out var days) || days is < 0 or > 60)
        {
            await messenger.SendTextAsync(settings.ChatId, "Укажи число дней от 0 до 60: /pause 3. /pause 0 — снять паузу.", cancellationToken);
            return;
        }

        var until = await progress.PauseAsync(settings, days, clock.Today(settings.TimeZoneId), cancellationToken);
        var reply = until is { } date
            ? $"⏸ Пауза до {date:dd.MM} включительно. Серия не сгорит. Вернуться раньше — /pause 0"
            : "▶️ Пауза снята, завтра утром пришлю сессию.";
        await messenger.SendTextAsync(settings.ChatId, reply, cancellationToken);
    }

    private async Task SettingsAsync(UserSettings settings, string argument, CancellationToken cancellationToken)
    {
        var parts = argument.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        switch (parts)
        {
            case ["time", var value] when TimeOnly.TryParseExact(value, ["H:mm", "HH:mm"], out var time):
                await settingsService.SetSendTimeAsync(settings, time, cancellationToken);
                await scheduler.EnsureScheduledAsync(settings, cancellationToken);
                await messenger.SendTextAsync(settings.ChatId, $"⏰ Теперь присылаю в {time:HH\\:mm} ({settings.TimeZoneId})", cancellationToken);
                return;

            case ["weekend", var value] when ParseWeekendMode(value) is { } mode:
                await settingsService.SetWeekendModeAsync(settings, mode, cancellationToken);
                await messenger.SendTextAsync(settings.ChatId, $"🗓 Выходные: {Describe(mode)}", cancellationToken);
                return;

            case []:
                break;

            default:
                await messenger.SendTextAsync(settings.ChatId, "Не понял настройку.", cancellationToken);
                break;
        }

        await messenger.SendTextAsync(
            settings.ChatId,
            $"⚙️ Настройки\n\n" +
            $"Время отправки: {settings.SendTime:HH\\:mm} ({settings.TimeZoneId})\n" +
            $"Выходные: {Describe(settings.WeekendMode)}\n" +
            (settings.PausedUntil is { } until ? $"Пауза до: {until:dd.MM}\n" : "") +
            "\nИзменить:\n/settings time 07:30\n/settings weekend review | full | off",
            cancellationToken);
    }

    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken cancellationToken)
    {
        var data = callback.Data ?? "";
        var settings = await settingsService.GetAsync(cancellationToken);
        if (settings is null)
        {
            await AnswerAsync(callback, "Сначала /start", cancellationToken: cancellationToken);
            return;
        }

        if (CallbackData.TryParseFeedback(data, out var digestId, out var feedback))
        {
            var result = await progress.ApplyFeedbackAsync(digestId, feedback, clock.Today(settings.TimeZoneId), cancellationToken);
            var answer = result.Outcome switch
            {
                FeedbackOutcome.Applied => DescribeFeedback(result),
                FeedbackOutcome.AlreadyGiven => $"Уже отмечено: {MessageRenderer.FeedbackLabel(result.Feedback!.Value)}",
                _ => "Набор не найден",
            };

            await AnswerAsync(callback, answer, cancellationToken: cancellationToken);
            await RemovePressedRowAsync(callback, cancellationToken);
            return;
        }

        if (CallbackData.TryParseSolution(data, out var taskId))
        {
            await AnswerAsync(callback, cancellationToken: cancellationToken);
            await RemovePressedRowAsync(callback, cancellationToken);

            var task = await practice.GetTaskAsync(taskId, cancellationToken);
            if (task is not null)
            {
                await practice.RevealSolutionAsync(settings.ChatId, task, cancellationToken);
            }

            return;
        }

        if (CallbackData.TryParseSubmit(data, out var submitTaskId))
        {
            // кнопку под задачей не убираем: решение можно отправлять несколько раз
            await AnswerAsync(callback, cancellationToken: cancellationToken);
            if (await practice.GetTaskAsync(submitTaskId, cancellationToken) is { } task)
            {
                await practice.StartAwaitingSolutionAsync(settings.ChatId, task, cancellationToken);
            }

            return;
        }

        if (CallbackData.TryParseCancelSubmit(data, out var cancelTaskId))
        {
            await practice.CancelAwaitingSolutionAsync(cancelTaskId, cancellationToken);
            await AnswerAsync(callback, "Ок, это не решение. Спрашивай.", cancellationToken: cancellationToken);
            await RemovePressedRowAsync(callback, cancellationToken);
            return;
        }

        if (CallbackData.TryParseHint(data, out var hintTaskId))
        {
            await AnswerAsync(callback, cancellationToken: cancellationToken);
            if (await practice.GetTaskAsync(hintTaskId, cancellationToken) is { } task)
            {
                await practice.GiveHintAsync(settings, task, cancellationToken);
            }

            return;
        }

        if (CallbackData.TryParseCheckMessage(data, out var messageId))
        {
            await AnswerAsync(callback, cancellationToken: cancellationToken);
            // убираем кнопку до ревью: повторное нажатие во время долгого запроса не создаст вторую попытку
            await RemovePressedRowAsync(callback, cancellationToken);
            if (!await practice.SubmitMessageAsSolutionAsync(settings, messageId, cancellationToken))
            {
                await messenger.SendTextAsync(settings.ChatId, "Это сообщение уже проверено как решение.", cancellationToken);
            }

            return;
        }

        if (CallbackData.TryParseAdvance(data, out var advanceTaskId))
        {
            await AnswerAsync(callback, cancellationToken: cancellationToken);
            await RemovePressedRowAsync(callback, cancellationToken);
            if (await practice.GetTaskAsync(advanceTaskId, cancellationToken) is { } task)
            {
                await practice.AdvanceAsync(settings, task, cancellationToken);
            }

            return;
        }

        if (data is CallbackData.ExtraSql or CallbackData.ExtraCSharp)
        {
            await AnswerAsync(callback, cancellationToken: cancellationToken);
            await RemovePressedRowAsync(callback, cancellationToken);
            await ExtraTaskAsync(settings, data == CallbackData.ExtraSql ? TaskLanguage.Sql : TaskLanguage.CSharp, cancellationToken);
            return;
        }

        if (data is CallbackData.NextTask or CallbackData.NewTopic)
        {
            await AnswerAsync(callback, cancellationToken: cancellationToken);
            // Убираем кнопки до генерации: повторное нажатие во время долгого запроса не запустит её второй раз
            await RemovePressedRowAsync(callback, cancellationToken);

            await (data == CallbackData.NextTask
                ? NextAsync(settings, cancellationToken)
                : NewTopicAsync(settings, cancellationToken));
            return;
        }

        await AnswerAsync(callback, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Гасит «часики» на кнопке. Это косметика: если Telegram в этот момент недоступен,
    /// само действие кнопки всё равно должно выполниться, а не оборваться на подтверждении.
    /// </summary>
    private async Task AnswerAsync(CallbackQuery callback, string? text = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await bot.AnswerCallbackQuery(callback.Id, text, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to answer callback query {CallbackId}", callback.Id);
        }
    }

    /// <summary>
    /// Убирает с сообщения ряд кнопок, в котором нажали кнопку; остальные ряды остаются
    /// (например, после «Показать решение» остаются «Ещё задача» и «Новая тема»).
    /// Сбой редактирования не критичен — только логируем.
    /// </summary>
    private async Task RemovePressedRowAsync(CallbackQuery callback, CancellationToken cancellationToken)
    {
        if (callback.Message is not { } message)
        {
            return;
        }

        var remaining = (message.ReplyMarkup?.InlineKeyboard ?? [])
            .Where(row => !row.Any(button => button.CallbackData == callback.Data))
            .Select(row => row.ToArray())
            .ToList();

        try
        {
            await bot.EditMessageReplyMarkup(
                message.Chat.Id,
                message.MessageId,
                replyMarkup: remaining.Count > 0 ? new InlineKeyboardMarkup(remaining) : null,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to update keyboard of message {MessageId}", message.MessageId);
        }
    }

    private static string DescribeFeedback(FeedbackResult result) => result.Repetition switch
    {
        { IsMastered: true } => "✅ Тема освоена! Больше не буду её повторять",
        { Status: TopicStatus.Understood, NextReviewOn: { } next } => $"✅ Понял. Повторим {next:dd.MM}",
        { Status: TopicStatus.Repeat } => "🔁 Хорошо, вернусь к теме завтра",
        _ => "❌ Отметил как пробел — тема в начале очереди",
    };

    private static WeekendMode? ParseWeekendMode(string value) => value.ToLowerInvariant() switch
    {
        "review" => WeekendMode.ReviewTask,
        "full" => WeekendMode.Full,
        "off" => WeekendMode.Off,
        _ => null,
    };

    private static string Describe(WeekendMode mode) => mode switch
    {
        WeekendMode.ReviewTask => "одна задача-повторение (review)",
        WeekendMode.Full => "полный набор (full)",
        WeekendMode.Off => "ничего не присылать (off)",
        _ => mode.ToString(),
    };
}
