using System.ComponentModel.DataAnnotations;
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

public sealed class AnthropicContentGenerator(
    AnthropicClient client,
    IOptions<AnthropicOptions> options,
    ILogger<AnthropicContentGenerator> logger) : StructuredContentGenerator(logger)
{
    protected override async Task<string> CompleteJsonAsync(
        string purpose, string systemPrompt, string userPrompt, ContentSchema schema, CancellationToken cancellationToken)
    {
        var parameters = new MessageCreateParams
        {
            Model = options.Value.Model,
            MaxTokens = options.Value.MaxTokens,
            System = systemPrompt,
            Messages = [new() { Role = Role.User, Content = userPrompt }],
            OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = schema.ToDictionary() } },
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

        return string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
    }
}
