using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace InterviewBot.Infrastructure.Content;

public interface IContentGenerator
{
    Task<GeneratedTheory> GenerateTheoryAsync(TopicContext topic, CancellationToken cancellationToken);

    Task<IReadOnlyList<GeneratedTask>> GenerateTasksAsync(
        TopicContext topic, IReadOnlyList<TaskRequest> tasks, IReadOnlyList<string> previousTitles, CancellationToken cancellationToken);

    Task<SnippetSelection> SelectSnippetAsync(TopicContext topic, string repo, string path, string numberedWindow, CancellationToken cancellationToken);

    /// <summary>Ответ интервьюера на сообщение в диалоге: без оценки.</summary>
    Task<DialogueReply> ReplyInDialogueAsync(
        PracticeTaskView task, IReadOnlyList<DialogueTurn> history, string message, CancellationToken cancellationToken);

    Task<string> GiveHintAsync(PracticeTaskView task, IReadOnlyList<DialogueTurn> history, int hintNumber, CancellationToken cancellationToken);

    Task<AttemptReview> ReviewAttemptAsync(
        PracticeTaskView task, IReadOnlyList<DialogueTurn> history, int hintsUsed, string answer, CancellationToken cancellationToken);
}

public sealed class ContentGenerationException(string message, Exception? inner = null) : Exception(message, inner);

public enum ContentProvider
{
    Anthropic,
    OpenAICompatible,
}

public sealed class ContentOptions
{
    public const string SectionName = "Content";

    public ContentProvider Provider { get; init; } = ContentProvider.OpenAICompatible;
}

/// <summary>
/// Общая часть всех провайдеров: промпты, схемы, разбор ответа и проверка результата.
/// Наследник отвечает только за один вызов модели, который должен вернуть JSON по схеме.
/// </summary>
public abstract class StructuredContentGenerator(ILogger logger) : IContentGenerator
{
    private const int MaxJsonAttempts = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<GeneratedTheory> GenerateTheoryAsync(TopicContext topic, CancellationToken cancellationToken) =>
        GenerateAsync<GeneratedTheory>("theory", Prompts.Theory(topic), ContentSchemas.Theory, cancellationToken);

    public async Task<IReadOnlyList<GeneratedTask>> GenerateTasksAsync(
        TopicContext topic, IReadOnlyList<TaskRequest> tasks, IReadOnlyList<string> previousTitles, CancellationToken cancellationToken)
    {
        var result = await GenerateAsync<GeneratedTasks>(
            "tasks", Prompts.Tasks(topic, tasks, previousTitles), ContentSchemas.Tasks, cancellationToken);

        // сессия строится по номерам задач: недостающая задача сломала бы порядок шагов
        return result.Tasks?.Count >= tasks.Count
            ? result.Tasks
            : throw new ContentGenerationException($"Model returned {result.Tasks?.Count ?? 0} tasks instead of {tasks.Count}");
    }

    public Task<SnippetSelection> SelectSnippetAsync(
        TopicContext topic, string repo, string path, string numberedWindow, CancellationToken cancellationToken) =>
        GenerateAsync<SnippetSelection>(
            "snippet", Prompts.SnippetSelection(topic, repo, path, numberedWindow), ContentSchemas.SnippetSelection, cancellationToken);

    public Task<DialogueReply> ReplyInDialogueAsync(
        PracticeTaskView task, IReadOnlyList<DialogueTurn> history, string message, CancellationToken cancellationToken) =>
        GenerateAsync<DialogueReply>("dialogue", Prompts.DialogueReply(task, history, message), ContentSchemas.DialogueReply, cancellationToken);

    public async Task<string> GiveHintAsync(
        PracticeTaskView task, IReadOnlyList<DialogueTurn> history, int hintNumber, CancellationToken cancellationToken) =>
        (await GenerateAsync<DialogueReply>("hint", Prompts.Hint(task, history, hintNumber), ContentSchemas.DialogueReply, cancellationToken)).Reply;

    public Task<AttemptReview> ReviewAttemptAsync(
        PracticeTaskView task, IReadOnlyList<DialogueTurn> history, int hintsUsed, string answer, CancellationToken cancellationToken) =>
        GenerateAsync<AttemptReview>(
            "review", Prompts.AttemptReview(task, history, hintsUsed, answer), ContentSchemas.AttemptReview, cancellationToken);

    /// <summary>Один вызов модели. Возвращает текст ответа — JSON по схеме.</summary>
    protected abstract Task<string> CompleteJsonAsync(string purpose, string systemPrompt, string userPrompt, ContentSchema schema, CancellationToken cancellationToken);

    private async Task<T> GenerateAsync<T>(string purpose, string prompt, ContentSchema schema, CancellationToken cancellationToken)
    {
        // Не все модели строго соблюдают схему: битый JSON — повод для одной повторной попытки, а не для падения
        for (var attempt = 1; ; attempt++)
        {
            // запрос к модели идёт десятки секунд: без этой строки по логам не отличить «думает» от «завис»
            logger.LogInformation("Requesting {Purpose} from model, attempt {Attempt}", purpose, attempt);
            var json = await CompleteJsonAsync(purpose, Prompts.System, prompt, schema, cancellationToken);
            try
            {
                return JsonSerializer.Deserialize<T>(StripCodeFence(json), JsonOptions)
                    ?? throw new JsonException("Response is empty");
            }
            catch (JsonException ex) when (attempt < MaxJsonAttempts)
            {
                logger.LogWarning(ex, "Invalid JSON from model for {Purpose}, attempt {Attempt}", purpose, attempt);
            }
            catch (JsonException ex)
            {
                throw new ContentGenerationException($"Model returned invalid JSON for {purpose}", ex);
            }
        }
    }

    /// <summary>Некоторые модели оборачивают JSON в ```json ... ``` даже в режиме структурированного ответа.</summary>
    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine >= 0 && lastFence > firstNewLine ? trimmed[(firstNewLine + 1)..lastFence] : trimmed;
    }
}
