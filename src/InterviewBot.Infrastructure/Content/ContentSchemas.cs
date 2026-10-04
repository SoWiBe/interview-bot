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
          "required": ["language", "title", "statement", "expectedQuestions", "referenceSolution", "solutionExplanation", "complexity"],
          "properties": {
            "language": { "type": "string", "enum": ["csharp", "sql"] },
            "title": { "type": "string" },
            "statement": { "type": "string" },
            "expectedQuestions": { "type": "array", "items": { "type": "string" } },
            "referenceSolution": { "type": "string" },
            "solutionExplanation": { "type": "string" },
            "complexity": { "type": "string" }
          }
        }
        """;

    public static readonly Dictionary<string, JsonElement> DailyContent = Parse($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["theory", "tasks"],
          "properties": {
            "theory": {
              "type": "object",
              "additionalProperties": false,
              "required": ["essence", "analogy", "productionCase", "codeExample", "codeLanguage", "selfCheckQuestions"],
              "properties": {
                "essence": { "type": "string" },
                "analogy": { "type": "string" },
                "productionCase": { "type": "string" },
                "codeExample": { "type": "string" },
                "codeLanguage": { "type": "string", "enum": ["csharp", "sql"] },
                "selfCheckQuestions": { "type": "array", "items": { "type": "string" } }
              }
            },
            "tasks": { "type": "array", "items": {{TaskSchema}} }
          }
        }
        """);

    public static readonly Dictionary<string, JsonElement> Tasks = Parse($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["tasks"],
          "properties": {
            "tasks": { "type": "array", "items": {{TaskSchema}} }
          }
        }
        """);

    public static readonly Dictionary<string, JsonElement> SnippetSelection = Parse("""
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

    public static readonly Dictionary<string, JsonElement> AttemptReview = Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["askedClarifyingQuestions", "clarifyingFeedback", "missedQuestions", "correctness", "edgeCases", "complexity", "improvements", "improvedCode", "score"],
          "properties": {
            "askedClarifyingQuestions": { "type": "boolean" },
            "clarifyingFeedback": { "type": "string" },
            "missedQuestions": { "type": "array", "items": { "type": "string" } },
            "correctness": { "type": "string" },
            "edgeCases": { "type": "string" },
            "complexity": { "type": "string" },
            "improvements": { "type": "string" },
            "improvedCode": { "type": "string" },
            "score": { "type": "integer" }
          }
        }
        """);

    private static Dictionary<string, JsonElement> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
        ?? throw new InvalidOperationException("Invalid schema");
}
