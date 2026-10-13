using InterviewBot.Core.Topics;
using InterviewBot.Infrastructure.Content;
using InterviewBot.Infrastructure.GitHub;
using InterviewBot.Infrastructure.Persistence;
using Telegram.Bot.Types.ReplyMarkups;
using static InterviewBot.Infrastructure.Telegram.TelegramHtml;

namespace InterviewBot.Infrastructure.Telegram;

/// <summary>Что предложить под ревью или решением, чтобы пойти дальше.</summary>
public enum NextStep
{
    /// <summary>Задача вне сессии: предлагаем ещё задачу или новую тему.</summary>
    Free,

    /// <summary>В сессии есть следующая задача.</summary>
    NextTask,

    /// <summary>Это последняя задача сессии.</summary>
    FinishSession,
}

/// <summary>Строка итога сессии по одной задаче.</summary>
public sealed record SessionSummaryLine(PracticeTask Task, int? BestScore, bool AskedQuestions);

/// <summary>Вёрстка сообщений бота. Все тексты от модели проходят через экранирование.</summary>
public static class MessageRenderer
{
    public static string Theory(Topic topic, GeneratedTheory theory)
    {
        var blocks = new List<string>
        {
            $"📚 {Bold(topic.Title)}\n{Italic($"Модуль {topic.Module} · {topic.ModuleTitle}")}",
            $"{Bold("Суть")}\n{InlineMarkdown(theory.Essence)}",
            $"{Bold("🏠 На примере")}\n{InlineMarkdown(theory.Analogy)}",
            CodeBlock(theory.CodeExample, theory.CodeLanguage),
            $"{Bold("❓ Проверь себя — ответь вслух")}\n" +
                string.Join("\n", theory.SelfCheckQuestions.Select((q, i) => $"{i + 1}. {InlineMarkdown(q)}")),
        };

        return string.Join(BlockSeparator, blocks);
    }

    public static string Snippet(CodeSnippet snippet)
    {
        var location = $"{snippet.Repo} · {snippet.Path}#L{snippet.StartLine}-L{snippet.EndLine}";
        var blocks = new List<string>
        {
            $"🔍 {Bold("Код из реального проекта")}\n{Link(snippet.Permalink, location)}\n{Italic("коммит " + snippet.CommitSha[..Math.Min(10, snippet.CommitSha.Length)])}",
            CodeBlock(snippet.Code, "csharp"),
            $"{Bold("👍 Что здесь хорошо")}\n{InlineMarkdown(snippet.WhatIsGood)}",
            $"{Bold("🎒 Забрать себе")}\n{InlineMarkdown(snippet.Takeaway)}",
        };

        return string.Join(BlockSeparator, blocks);
    }

    public const string ReviewTaskHeader = "Задача-повторение";
    public const string ExtraTaskHeader = "Ещё задача";

    /// <param name="total">Сколько задач в сессии; 1 — задача вне сессии, номер не показываем.</param>
    /// <param name="collapseSchema">Схема уже была в предыдущей задаче: показываем свёрнутой, чтобы не листать чат.</param>
    public static IReadOnlyList<string> Task(PracticeTask task, int total, string? header = null, bool collapseSchema = false)
    {
        var title = header ?? (total > 1 ? $"Задача {task.OrderInDigest}/{total}" : "Задача");
        var blocks = new List<string>
        {
            $"🧩 {Bold($"{title} · {LanguageLabel(task.Language)}{RoleLabel(task)} · {TimeBudget(task)}")}\n{Escape(task.Title)}",
        };

        if (header == ReviewTaskHeader)
        {
            blocks.Add(Italic("Выходной: одна задача на повторение, без новой теории. Полная сессия — в понедельник утром, " +
                              "а прямо сейчас можно взять тему: /topic <название>"));
        }

        if (!string.IsNullOrWhiteSpace(task.Schema))
        {
            blocks.Add(collapseSchema
                ? $"<blockquote expandable>{Bold("🗄 Схема — та же, что в прошлой задаче")}\n{CodeBlock(task.Schema, "sql")}</blockquote>"
                : CodeBlock(task.Schema, "sql"));
        }

        blocks.AddRange(FromMarkdownLite(task.Statement));
        blocks.Add(Italic("Есть вопрос по условию или хочешь обсудить идею — просто напиши, это не оценивается. " +
                          "Готов сдать — жми «Отправить решение»."));

        return Pack(blocks);
    }

    public static InlineKeyboardMarkup TaskKeyboard(Guid taskId) => new(
    [
        [InlineKeyboardButton.WithCallbackData("📤 Отправить решение", CallbackData.Submit(taskId))],
        [
            InlineKeyboardButton.WithCallbackData("💡 Подсказка", CallbackData.Hint(taskId)),
            InlineKeyboardButton.WithCallbackData("🏳 Показать решение", CallbackData.Solution(taskId)),
        ],
    ]);

    public static string AwaitingSolution(PracticeTask task) =>
        $"✍️ Жду решение задачи «{Escape(task.Title)}» следующим сообщением.";

    public static InlineKeyboardMarkup AwaitingKeyboard(Guid taskId) => new(
        [[InlineKeyboardButton.WithCallbackData("↩️ Отмена, у меня ещё вопрос", CallbackData.CancelSubmit(taskId))]]);

    public const string LooksLikeSolution =
        "Похоже на готовое решение. Я его пока не оценивал. Проверить как решение?";

    public static InlineKeyboardMarkup CheckAsSolutionKeyboard(Guid messageId) => new(
        [[InlineKeyboardButton.WithCallbackData("📤 Проверить это как решение", CallbackData.CheckMessage(messageId))]]);

    public static IReadOnlyList<string> DialogueAnswer(string reply) => Pack(FromMarkdownLite(reply));

    public static IReadOnlyList<string> HintMessage(int number, string hint) =>
        Pack([$"💡 {Bold($"Подсказка {number}")}", .. FromMarkdownLite(hint)]);

    public static IReadOnlyList<string> Review(AttemptReview review, PracticeTask task, int hintsUsed)
    {
        var blocks = new List<string>
        {
            $"📝 {Bold($"Ревью · {Math.Clamp(review.Score, 1, 5)}/5 · {VerdictLabel(review.Verdict)}")}\n{InlineMarkdown(review.Summary)}",
        };

        if (review.Good.Count > 0)
        {
            blocks.Add($"{Bold("👍 Что хорошо")}\n" + string.Join("\n", review.Good.Select(g => "• " + InlineMarkdown(g))));
        }

        if (review.Fixes.Count > 0)
        {
            blocks.Add($"{Bold("🔧 Что поправить")}\n" + string.Join("\n\n", review.Fixes.Select((f, i) => $"{i + 1}. {InlineMarkdown(f)}")));
        }

        if (!string.IsNullOrWhiteSpace(review.ImprovedCode))
        {
            blocks.Add(CodeBlock(review.ImprovedCode, CodeLanguage(task.Language)));
        }

        var notes = new List<string>();
        if (task.ExpectedQuestions.Count > 0)
        {
            notes.Add(review.AskedClarifyingQuestions
                ? "✅ Уточняющий вопрос задан"
                : "❓ Уточняющий вопрос не задан — какой именно, увидишь в решении");
        }

        if (hintsUsed > 0)
        {
            notes.Add($"💡 Подсказок взято: {hintsUsed}");
        }

        if (notes.Count > 0)
        {
            blocks.Add(Italic(string.Join("\n", notes)));
        }

        return Pack(blocks);
    }

    /// <summary>Ревью простым текстом — для истории переписки, которую читает модель.</summary>
    public static string ReviewPlainText(AttemptReview review) =>
        $"[Ревью решения: {Math.Clamp(review.Score, 1, 5)}/5. {review.Summary}" +
        (review.Fixes.Count > 0 ? " Замечания: " + string.Join("; ", review.Fixes) : "") + "]";

    public static IReadOnlyList<string> Solution(PracticeTask task)
    {
        var blocks = new List<string> { $"👀 {Bold("Решение")} — {Escape(task.Title)}" };

        if (task.ExpectedQuestions.Count > 0)
        {
            blocks.Add($"{Bold("Что стоило уточнить")}\n" + string.Join("\n", task.ExpectedQuestions.Select(q => "• " + InlineMarkdown(q))));
        }

        blocks.Add(CodeBlock(task.ReferenceSolution, CodeLanguage(task.Language)));
        blocks.AddRange(FromMarkdownLite(task.SolutionExplanation));
        blocks.Add($"{Bold("Сложность")}: {InlineMarkdown(task.Complexity)}");

        return Pack(blocks);
    }

    public static string SessionSummary(IReadOnlyList<SessionSummaryLine> lines, int streak)
    {
        var blocks = new List<string> { $"🏁 {Bold("Сессия пройдена")}" };

        blocks.Add(string.Join("\n", lines.Select(l =>
            $"{l.Task.OrderInDigest}. {LanguageLabel(l.Task.Language)} · {Escape(l.Task.Title)} — " +
            (l.BestScore is { } score ? Bold($"{score}/5") : "без попытки"))));

        var withQuestions = lines.Where(l => l.Task.ExpectedQuestions.Count > 0 && l.BestScore is not null).ToList();
        if (withQuestions.Count > 0)
        {
            blocks.Add($"❓ Уточняющие вопросы: задал в {withQuestions.Count(l => l.AskedQuestions)} из {withQuestions.Count} задач, где они были нужны");
        }

        blocks.Add($"🔥 Серия: {Bold($"{streak} дн.")}");

        return string.Join(BlockSeparator, blocks);
    }

    private static string LanguageLabel(TaskLanguage language) => language == TaskLanguage.Sql ? "SQL" : "C#";

    private static string RoleLabel(PracticeTask task) => task.Role == TaskRole.Warmup ? " · разогрев" : "";

    private static string TimeBudget(PracticeTask task) => task switch
    {
        { Role: TaskRole.Warmup } => "~5 мин",
        { Language: TaskLanguage.Sql } => "~10–15 мин",
        _ => "~15 мин",
    };

    private static string VerdictLabel(string verdict) => verdict switch
    {
        ReviewVerdict.Works => "работает",
        ReviewVerdict.WorksWithRemarks => "работает с замечаниями",
        _ => "пока не работает",
    };

    private static string CodeLanguage(TaskLanguage language) => language == TaskLanguage.Sql ? "sql" : "csharp";

    public static InlineKeyboardMarkup FeedbackKeyboard(Guid digestId) => new([FeedbackRow(digestId)]);

    /// <summary>
    /// Кнопки под ревью и под решением: что делать дальше.
    /// <paramref name="retryTaskId"/> — «Отправить ещё раз»; <paramref name="solutionTaskId"/> — «Показать решение»;
    /// <paramref name="feedbackDigestId"/> — оценить тему (когда теории не было и кнопок под ней тоже, например у выходной задачи).
    /// </summary>
    public static InlineKeyboardMarkup NextStepsKeyboard(
        NextStep next, Guid taskId, Guid? retryTaskId = null, Guid? solutionTaskId = null, Guid? feedbackDigestId = null)
    {
        var rows = new List<InlineKeyboardButton[]>();

        if (next == NextStep.NextTask)
        {
            rows.Add([InlineKeyboardButton.WithCallbackData("➡️ Следующая задача", CallbackData.Advance(taskId))]);
        }
        else if (next == NextStep.FinishSession)
        {
            rows.Add([InlineKeyboardButton.WithCallbackData("🏁 Завершить сессию", CallbackData.Advance(taskId))]);
        }

        if (retryTaskId is { } retryId)
        {
            rows.Add([InlineKeyboardButton.WithCallbackData("🔁 Отправить ещё раз", CallbackData.Submit(retryId))]);
        }

        if (solutionTaskId is { } solutionId)
        {
            rows.Add([InlineKeyboardButton.WithCallbackData("👀 Показать решение", CallbackData.Solution(solutionId))]);
        }

        if (feedbackDigestId is { } digestId)
        {
            rows.Add(FeedbackRow(digestId));
        }

        if (next == NextStep.Free)
        {
            rows.Add(FreeRow);
        }

        return new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup FreeKeyboard => new([FreeRow]);

    public static InlineKeyboardMarkup ExtraTaskKeyboard => new(
    [[
        InlineKeyboardButton.WithCallbackData("SQL", CallbackData.ExtraSql),
        InlineKeyboardButton.WithCallbackData("C#", CallbackData.ExtraCSharp),
    ]]);

    private static InlineKeyboardButton[] FreeRow =>
    [
        InlineKeyboardButton.WithCallbackData("➕ Ещё задача", CallbackData.NextTask),
        InlineKeyboardButton.WithCallbackData("📚 Новая тема", CallbackData.NewTopic),
    ];

    private static InlineKeyboardButton[] FeedbackRow(Guid digestId) =>
    [
        InlineKeyboardButton.WithCallbackData("✅ Понял", CallbackData.Feedback(digestId, TopicFeedback.Understood)),
        InlineKeyboardButton.WithCallbackData("🔁 Повторить", CallbackData.Feedback(digestId, TopicFeedback.Repeat)),
        InlineKeyboardButton.WithCallbackData("❌ Пробел", CallbackData.Feedback(digestId, TopicFeedback.Gap)),
    ];

    public static string FeedbackPrompt => $"{Bold("Как тема?")} Отметь — от этого зависит, когда она вернётся на повторение.";

    public static string FeedbackLabel(TopicFeedback feedback) => feedback switch
    {
        TopicFeedback.Understood => "✅ Понял",
        TopicFeedback.Repeat => "🔁 Повторить",
        TopicFeedback.Gap => "❌ Пробел",
        _ => feedback.ToString(),
    };
}

/// <summary>callback_data ограничен 64 байтами: короткие префиксы + Guid в формате N (32 символа).</summary>
public static class CallbackData
{
    private const string FeedbackPrefix = "fb";
    private const string SolutionPrefix = "sol";
    private const string SubmitPrefix = "sub";
    private const string CancelSubmitPrefix = "cancel";
    private const string HintPrefix = "hint";
    private const string CheckMessagePrefix = "chk";
    private const string AdvancePrefix = "nxt";

    public const string NextTask = "next";
    public const string NewTopic = "newtopic";
    public const string ExtraSql = "extra:sql";
    public const string ExtraCSharp = "extra:cs";

    public static string Feedback(Guid digestId, TopicFeedback feedback) => $"{FeedbackPrefix}:{digestId:N}:{(int)feedback}";

    public static string Solution(Guid taskId) => $"{SolutionPrefix}:{taskId:N}";

    public static string Submit(Guid taskId) => $"{SubmitPrefix}:{taskId:N}";

    public static string CancelSubmit(Guid taskId) => $"{CancelSubmitPrefix}:{taskId:N}";

    public static string Hint(Guid taskId) => $"{HintPrefix}:{taskId:N}";

    /// <summary>Id — сообщения пользователя в переписке (TaskMessage), а не задачи.</summary>
    public static string CheckMessage(Guid messageId) => $"{CheckMessagePrefix}:{messageId:N}";

    /// <summary>Закрыть эту задачу и перейти к следующей в сессии.</summary>
    public static string Advance(Guid taskId) => $"{AdvancePrefix}:{taskId:N}";

    public static bool TryParseFeedback(string data, out Guid digestId, out TopicFeedback feedback)
    {
        var parts = data.Split(':');
        digestId = default;
        feedback = default;
        return parts is [FeedbackPrefix, var id, var value]
            && Guid.TryParseExact(id, "N", out digestId)
            && int.TryParse(value, out var raw)
            && Enum.IsDefined(feedback = (TopicFeedback)raw);
    }

    public static bool TryParseSolution(string data, out Guid taskId) => TryParseId(data, SolutionPrefix, out taskId);

    public static bool TryParseSubmit(string data, out Guid taskId) => TryParseId(data, SubmitPrefix, out taskId);

    public static bool TryParseCancelSubmit(string data, out Guid taskId) => TryParseId(data, CancelSubmitPrefix, out taskId);

    public static bool TryParseHint(string data, out Guid taskId) => TryParseId(data, HintPrefix, out taskId);

    public static bool TryParseCheckMessage(string data, out Guid messageId) => TryParseId(data, CheckMessagePrefix, out messageId);

    public static bool TryParseAdvance(string data, out Guid taskId) => TryParseId(data, AdvancePrefix, out taskId);

    private static bool TryParseId(string data, string prefix, out Guid id)
    {
        var parts = data.Split(':');
        id = default;
        return parts is [var actual, var value] && actual == prefix && Guid.TryParseExact(value, "N", out id);
    }
}
