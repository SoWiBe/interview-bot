namespace InterviewBot.Infrastructure.Content;

// DTO ответов модели. Имена свойств совпадают с JSON-схемами в ContentSchemas (camelCase).

public sealed record GeneratedTheory(
    string Essence,
    string Analogy,
    string CodeExample,
    string CodeLanguage,
    IReadOnlyList<string> SelfCheckQuestions);

public sealed record GeneratedTask(
    string Language,
    string Title,
    string Statement,
    string Schema,
    IReadOnlyList<string> ExpectedQuestions,
    IReadOnlyList<string> Clarifications,
    string ReferenceSolution,
    string SolutionExplanation,
    string Complexity);

public sealed record GeneratedTasks(IReadOnlyList<GeneratedTask> Tasks);

public sealed record SnippetSelection(bool Suitable, int StartLine, int EndLine, string WhatIsGood, string Takeaway);

public static class ReviewVerdict
{
    public const string Works = "works";
    public const string WorksWithRemarks = "worksWithRemarks";
    public const string DoesNotWork = "doesNotWork";
}

public sealed record AttemptReview(
    string Verdict,
    string Summary,
    IReadOnlyList<string> Good,
    IReadOnlyList<string> Fixes,
    string ImprovedCode,
    bool AskedClarifyingQuestions,
    int Score);

/// <summary>Ответ бота в диалоге по задаче.</summary>
/// <param name="LooksLikeSolution">Сообщение студента — готовое решение, а не вопрос: его надо сдавать, а не обсуждать.</param>
public sealed record DialogueReply(string Reply, bool LooksLikeSolution);

/// <summary>Реплика из переписки по задаче — контекст для модели.</summary>
public sealed record DialogueTurn(bool FromStudent, string Text);

/// <summary>Что сгенерировать в задаче: язык, объём и содержание.</summary>
/// <param name="Warmup">Разогрев: одна конструкция, ~5 минут, без неоднозначностей.</param>
public sealed record TaskRequest(string Language, string Focus, bool Warmup = false);

public sealed record TopicContext(string Title, string ModuleTitle);
