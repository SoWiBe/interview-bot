using InterviewBot.Core.Topics;
using InterviewBot.Infrastructure.Content;
using InterviewBot.Infrastructure.GitHub;
using InterviewBot.Infrastructure.Persistence;
using Telegram.Bot.Types.ReplyMarkups;
using static InterviewBot.Infrastructure.Telegram.TelegramHtml;

namespace InterviewBot.Infrastructure.Telegram;

/// <summary>Вёрстка сообщений бота. Все тексты от модели проходят через экранирование.</summary>
public static class MessageRenderer
{
    public const string TimerLine = "⏱ 15 минут. Сначала запиши минимум два уточняющих вопроса.";

    public static string Theory(Topic topic, GeneratedTheory theory)
    {
        var blocks = new List<string>
        {
            $"📚 {Bold(topic.Title)}\n{Italic($"Модуль {topic.Module} · {topic.ModuleTitle}")}",
            $"{Bold("Суть")}\n{InlineMarkdown(theory.Essence)}",
            $"{Bold("🏠 Аналогия")}\n{InlineMarkdown(theory.Analogy)}",
            $"{Bold("🔥 В продакшене")}\n{InlineMarkdown(theory.ProductionCase)}",
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

    public static IReadOnlyList<string> Task(PracticeTask task, int number, string? header = null)
    {
        var language = task.Language == TaskLanguage.Sql ? "SQL" : "C#";
        var blocks = new List<string>
        {
            $"🧩 {Bold($"{header ?? $"Задача {number}"} · {language}")} — {Escape(task.Title)}",
        };
        blocks.AddRange(FromMarkdownLite(task.Statement));
        blocks.Add(Escape(TimerLine));
        blocks.Add(Italic("Ответь reply на это сообщение: вопросы + решение. Сдаться — /giveup"));

        return Pack(blocks);
    }

    public static IReadOnlyList<string> Review(AttemptReview review, TaskLanguage language)
    {
        var questionsMark = review.AskedClarifyingQuestions ? "✅" : "❌";
        var blocks = new List<string>
        {
            $"📝 {Bold($"Ревью · {review.Score}/5")}",
            $"{Bold($"{questionsMark} Уточняющие вопросы")}\n{InlineMarkdown(review.ClarifyingFeedback)}",
        };

        if (review.MissedQuestions.Count > 0)
        {
            blocks.Add($"{Bold("Стоило спросить")}\n" + string.Join("\n", review.MissedQuestions.Select(q => "• " + InlineMarkdown(q))));
        }

        blocks.Add($"{Bold("Корректность")}\n{InlineMarkdown(review.Correctness)}");
        blocks.Add($"{Bold("Краевые случаи")}\n{InlineMarkdown(review.EdgeCases)}");
        blocks.Add($"{Bold("Сложность")}\n{InlineMarkdown(review.Complexity)}");
        blocks.Add(Bold("Как улучшить"));
        blocks.AddRange(FromMarkdownLite(review.Improvements));

        if (!string.IsNullOrWhiteSpace(review.ImprovedCode))
        {
            blocks.Add(CodeBlock(review.ImprovedCode, CodeLanguage(language)));
        }

        return Pack(blocks);
    }

    public static IReadOnlyList<string> Solution(PracticeTask task)
    {
        var blocks = new List<string> { $"👀 {Bold("Решение")} — {Escape(task.Title)}" };

        if (task.ExpectedQuestions.Count > 0)
        {
            blocks.Add($"{Bold("Уточняющие вопросы")}\n" + string.Join("\n", task.ExpectedQuestions.Select(q => "• " + InlineMarkdown(q))));
        }

        blocks.Add(CodeBlock(task.ReferenceSolution, CodeLanguage(task.Language)));
        blocks.AddRange(FromMarkdownLite(task.SolutionExplanation));
        blocks.Add($"{Bold("Сложность")}: {InlineMarkdown(task.Complexity)}");

        return Pack(blocks);
    }

    private static string CodeLanguage(TaskLanguage language) => language == TaskLanguage.Sql ? "sql" : "csharp";

    public static InlineKeyboardMarkup FeedbackKeyboard(Guid digestId) => new(
    [
        [
            InlineKeyboardButton.WithCallbackData("✅ Понял", CallbackData.Feedback(digestId, TopicFeedback.Understood)),
            InlineKeyboardButton.WithCallbackData("🔁 Повторить", CallbackData.Feedback(digestId, TopicFeedback.Repeat)),
            InlineKeyboardButton.WithCallbackData("❌ Пробел", CallbackData.Feedback(digestId, TopicFeedback.Gap)),
        ],
    ]);

    public static InlineKeyboardMarkup SolutionKeyboard(Guid taskId) =>
        new([[InlineKeyboardButton.WithCallbackData("👀 Показать решение", CallbackData.Solution(taskId))]]);

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

    public static string Feedback(Guid digestId, TopicFeedback feedback) => $"{FeedbackPrefix}:{digestId:N}:{(int)feedback}";

    public static string Solution(Guid taskId) => $"{SolutionPrefix}:{taskId:N}";

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

    public static bool TryParseSolution(string data, out Guid taskId)
    {
        var parts = data.Split(':');
        taskId = default;
        return parts is [SolutionPrefix, var id] && Guid.TryParseExact(id, "N", out taskId);
    }
}
