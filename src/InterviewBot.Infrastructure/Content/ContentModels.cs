namespace InterviewBot.Infrastructure.Content;

// DTO ответов модели. Имена свойств совпадают с JSON-схемами в ContentSchemas (camelCase).

public sealed record GeneratedTheory(
    string Essence,
    string Analogy,
    string ProductionCase,
    string CodeExample,
    string CodeLanguage,
    IReadOnlyList<string> SelfCheckQuestions);

public sealed record GeneratedTask(
    string Language,
    string Title,
    string Statement,
    IReadOnlyList<string> ExpectedQuestions,
    string ReferenceSolution,
    string SolutionExplanation,
    string Complexity);

public sealed record GeneratedDailyContent(GeneratedTheory Theory, IReadOnlyList<GeneratedTask> Tasks);

public sealed record GeneratedTasks(IReadOnlyList<GeneratedTask> Tasks);

public sealed record SnippetSelection(bool Suitable, int StartLine, int EndLine, string WhatIsGood, string Takeaway);

public sealed record AttemptReview(
    bool AskedClarifyingQuestions,
    string ClarifyingFeedback,
    IReadOnlyList<string> MissedQuestions,
    string Correctness,
    string EdgeCases,
    string Complexity,
    string Improvements,
    string ImprovedCode,
    int Score);

/// <summary>Что сгенерировать в задаче: язык и роль задачи в наборе.</summary>
public sealed record TaskRequest(string Language, string Focus);

public sealed record TopicContext(string Title, string ModuleTitle);
