using System.ClientModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace InterviewBot.Infrastructure.Content;

/// <summary>
/// Любой провайдер с OpenAI-совместимым Chat Completions API: OpenRouter (по умолчанию), DeepSeek, Ollama и т. п.
/// </summary>
public sealed class OpenAICompatibleOptions
{
    public const string SectionName = "OpenAICompatible";

    [Required]
    public string ApiKey { get; init; } = string.Empty;

    [Required, Url]
    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1";

    [Required]
    public string Model { get; init; } = "deepseek/deepseek-v4-pro-0813";

    [Range(1024, 64000)]
    public int MaxTokens { get; init; } = 16000;
}

public sealed class OpenAICompatibleContentGenerator(
    ChatClient client,
    IOptions<OpenAICompatibleOptions> options,
    ILogger<OpenAICompatibleContentGenerator> logger) : StructuredContentGenerator(logger)
{
    protected override async Task<string> CompleteJsonAsync(
        string purpose, string systemPrompt, string userPrompt, ContentSchema schema, CancellationToken cancellationToken)
    {
        var completionOptions = new ChatCompletionOptions
        {
            MaxOutputTokenCount = options.Value.MaxTokens,
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                schema.Name, BinaryData.FromString(schema.Json), jsonSchemaIsStrict: true),
        };

        ChatMessage[] messages = [new SystemChatMessage(systemPrompt), new UserChatMessage(userPrompt)];

        var started = TimeProvider.System.GetTimestamp();
        ChatCompletion completion;
        try
        {
            completion = await client.CompleteChatAsync(messages, completionOptions, cancellationToken);
        }
        catch (ClientResultException ex)
        {
            // 402 — кончились кредиты, 401 — неверный ключ: эти ошибки ретраить бессмысленно, нужен понятный текст
            throw new ContentGenerationException($"{options.Value.Model} {purpose} request failed: HTTP {ex.Status} {ex.Message}", ex);
        }

        logger.LogInformation(
            "{Model} {Purpose} completed in {ElapsedMs} ms: finish {FinishReason}, tokens in {InputTokens} / out {OutputTokens}",
            options.Value.Model,
            purpose,
            (long)TimeProvider.System.GetElapsedTime(started).TotalMilliseconds,
            completion.FinishReason,
            completion.Usage?.InputTokenCount,
            completion.Usage?.OutputTokenCount);

        if (!string.IsNullOrEmpty(completion.Refusal) || completion.FinishReason == ChatFinishReason.ContentFilter)
        {
            throw new ContentGenerationException($"Model declined the {purpose} request");
        }

        if (completion.FinishReason == ChatFinishReason.Length)
        {
            throw new ContentGenerationException($"Model {purpose} response was cut off by max tokens");
        }

        return string.Concat(completion.Content.Select(part => part.Text));
    }
}
