using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InterviewBot.Infrastructure.Content;

public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    [Required]
    public string ApiKey { get; init; } = string.Empty;

    [Required]
    public string Model { get; init; } = "claude-sonnet-5-5";

    [Range(1024, 64000)]
    public int MaxTokens { get; init; } = 16000;
}

public interface IContentGenerator
{
    Task<GeneratedDailyContent> GenerateDailyAsync(TopicContext topic, IReadOnlyList<TaskRequest> tasks, CancellationToken cancellationToken);

    Task<IReadOnlyList<GeneratedTask>> GenerateTasksAsync(
        TopicContext topic, IReadOnlyList<TaskRequest> tasks, IReadOnlyList<string> previousTitles, CancellationToken cancellationToken);

    Task<SnippetSelection> SelectSnippetAsync(TopicContext topic, string repo, string path, string numberedWindow, CancellationToken cancellationToken);

    Task<AttemptReview> ReviewAttemptAsync(PracticeTaskView task, string answer, CancellationToken cancellationToken);
}

public sealed class ContentGenerationException(string message) : Exception(message);

public sealed class AnthropicContentGenerator(
    AnthropicClient client,
    IOptions<AnthropicOptions> options,
    ILogger<AnthropicContentGenerator> logger) : IContentGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GeneratedDailyContent> GenerateDailyAsync(
        TopicContext topic, IReadOnlyList<TaskRequest> tasks, CancellationToken cancellationToken)
    {
        var content = await GenerateAsync<GeneratedDailyContent>(
            "daily", Prompts.DailyContent(topic, tasks), ContentSchemas.DailyContent, cancellationToken);

        if (content.Tasks.Count == 0)
        {
            throw new ContentGenerationException("Model returned no tasks");
        }

        return content;
    }

    public async Task<IReadOnlyList<GeneratedTask>> GenerateTasksAsync(
        TopicContext topic, IReadOnlyList<TaskRequest> tasks, IReadOnlyList<string> previousTitles, CancellationToken cancellationToken)
    {
        var result = await GenerateAsync<GeneratedTasks>(
            "tasks", Prompts.Tasks(topic, tasks, previousTitles), ContentSchemas.Tasks, cancellationToken);

        return result.Tasks.Count > 0 ? result.Tasks : throw new ContentGenerationException("Model returned no tasks");
    }

    public Task<SnippetSelection> SelectSnippetAsync(
        TopicContext topic, string repo, string path, string numberedWindow, CancellationToken cancellationToken) =>
        GenerateAsync<SnippetSelection>(
            "snippet", Prompts.SnippetSelection(topic, repo, path, numberedWindow), ContentSchemas.SnippetSelection, cancellationToken);

    public Task<AttemptReview> ReviewAttemptAsync(PracticeTaskView task, string answer, CancellationToken cancellationToken) =>
        GenerateAsync<AttemptReview>("review", Prompts.AttemptReview(task, answer), ContentSchemas.AttemptReview, cancellationToken);

    private async Task<T> GenerateAsync<T>(
        string purpose, string prompt, Dictionary<string, JsonElement> schema, CancellationToken cancellationToken)
    {
        var parameters = new MessageCreateParams
        {
            Model = options.Value.Model,
            MaxTokens = options.Value.MaxTokens,
            System = Prompts.System,
            Messages = [new() { Role = Role.User, Content = prompt }],
            OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = schema } },
        };

        var started = TimeProvider.System.GetTimestamp();
        var response = await client.Messages.Create(parameters, cancellationToken);

        logger.LogInformation(
            "Claude {Purpose} completed in {ElapsedMs} ms: stop {StopReason}, tokens in {InputTokens} / out {OutputTokens}",
            purpose,
            (long)TimeProvider.System.GetElapsedTime(started).TotalMilliseconds,
            response.StopReason,
            response.Usage.InputTokens,
            response.Usage.OutputTokens);

        // stop_reason проверяем до чтения content: при отказе или обрезке JSON будет неполным
        if (response.StopReason == "refusal")
        {
            throw new ContentGenerationException($"Claude declined the {purpose} request");
        }

        if (response.StopReason == "max_tokens")
        {
            throw new ContentGenerationException($"Claude {purpose} response was cut off by max_tokens");
        }

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));

        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new ContentGenerationException($"Claude {purpose} response is empty");
    }
}
