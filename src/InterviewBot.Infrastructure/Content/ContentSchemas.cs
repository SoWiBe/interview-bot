using System.Text.Json;

namespace InterviewBot.Infrastructure.Content;

/// <summary>
/// JSON-схемы для structured outputs: модель обязана вернуть ровно такую структуру,
/// поэтому ответ десериализуется без «вытаскивания JSON из текста».
/// </summary>
internal static class ContentSchemas
{
    private const string TaskSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["language", "title", "statement", "schema", "expectedQuestions", "clarifications", "referenceSolution", "solutionExplanation", "complexity"],
          "properties": {
            "language": { "type": "string", "enum": ["csharp", "sql"] },
            "title": { "type": "string" },
            "statement": { "type": "string" },
            "schema": { "type": "string" },
            "expectedQuestions": { "type": "array", "items": { "type": "string" } },
            "clarifications": { "type": "array", "items": { "type": "string" } },
            "referenceSolution": { "type": "string" },
            "solutionExplanation": { "type": "string" },
            "complexity": { "type": "string" }
          }
        }
        """;

    public static readonly ContentSchema Theory = new("theory", """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["essence", "analogy", "codeExample", "codeLanguage", "selfCheckQuestions"],
          "properties": {
            "essence": { "type": "string" },
            "analogy": { "type": "string" },
            "codeExample": { "type": "string" },
            "codeLanguage": { "type": "string", "enum": ["csharp", "sql"] },
            "selfCheckQuestions": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

    public static readonly ContentSchema Tasks = new("tasks", $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["tasks"],
          "properties": {
            "tasks": { "type": "array", "items": {{TaskSchema}} }
          }
        }
        """);

    public static readonly ContentSchema SnippetSelection = new("snippet_selection", """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["suitable", "startLine", "endLine", "whatIsGood", "takeaway"],
          "properties": {
            "suitable": { "type": "boolean" },
            "startLine": { "type": "integer" },
            "endLine": { "type": "integer" },
            "whatIsGood": { "type": "string" },
            "takeaway": { "type": "string" }
          }
        }
        """);

    public static readonly ContentSchema AttemptReview = new("attempt_review", """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["verdict", "summary", "good", "fixes", "improvedCode", "askedClarifyingQuestions", "score"],
          "properties": {
            "verdict": { "type": "string", "enum": ["works", "worksWithRemarks", "doesNotWork"] },
            "summary": { "type": "string" },
            "good": { "type": "array", "items": { "type": "string" } },
            "fixes": { "type": "array", "items": { "type": "string" } },
            "improvedCode": { "type": "string" },
            "askedClarifyingQuestions": { "type": "boolean" },
            "score": { "type": "integer" }
          }
        }
        """);

    public static readonly ContentSchema DialogueReply = new("dialogue_reply", """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["reply", "looksLikeSolution"],
          "properties": {
            "reply": { "type": "string" },
            "looksLikeSolution": { "type": "boolean" }
          }
        }
        """);
}

/// <summary>JSON-схема ответа: имя (нужно OpenAI-совместимым API) и сам текст схемы.</summary>
public sealed record ContentSchema(string Name, string Json)
{
    public Dictionary<string, JsonElement> ToDictionary() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Json)
        ?? throw new InvalidOperationException($"Invalid schema {Name}");
}
