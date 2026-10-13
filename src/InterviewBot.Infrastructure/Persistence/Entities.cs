using InterviewBot.Core.Progress;
using InterviewBot.Core.Schedule;
using InterviewBot.Core.Topics;

namespace InterviewBot.Infrastructure.Persistence;

public sealed class UserSettings
{
    public long TelegramUserId { get; set; }
    public long ChatId { get; set; }
    public TimeOnly SendTime { get; set; } = new(7, 0);
    public string TimeZoneId { get; set; } = "Asia/Omsk";
    public WeekendMode WeekendMode { get; set; } = WeekendMode.ReviewTask;
    public DateOnly? PausedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Topic
{
    public required string Id { get; set; }
    public int Module { get; set; }
    public required string ModuleTitle { get; set; }
    public required string Title { get; set; }
    public int Priority { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<CodeRef> CodeRefs { get; set; } = [];

    public TopicProgress? Progress { get; set; }
}

public sealed class CodeRef
{
    public required string Repo { get; set; }
    public required string Path { get; set; }
    public required string Symbol { get; set; }
}

public sealed class TopicProgress
{
    public required string TopicId { get; set; }
    public TopicStatus Status { get; set; }
    public int Stage { get; set; }
    public DateOnly? NextReviewOn { get; set; }
    public DateOnly? LastShownOn { get; set; }
    public DateOnly StatusChangedOn { get; set; }
}

public enum DigestKind
{
    /// <summary>Утренний набор: не больше одного на дату (уникальный индекс).</summary>
    Daily,

    /// <summary>Внеочередная тема по /topic.</summary>
    Manual,
}

/// <summary>Сгенерированный контент на день. Хранится готовым HTML, чтобы повторно не генерировать.</summary>
public sealed class Digest
{
    public Guid Id { get; set; }
    public DigestKind Kind { get; set; }
    public DateOnly ForDate { get; set; }
    public required string TopicId { get; set; }
    public Topic? Topic { get; set; }

    public string? TheoryHtml { get; set; }
    public string? SnippetHtml { get; set; }
    public string? SnippetPermalink { get; set; }

    public TopicFeedback? Feedback { get; set; }

    // Факты отправки по частям — основа идемпотентности (см. DigestDelivery)
    public DateTimeOffset? TheorySentAt { get; set; }
    public DateTimeOffset? SnippetSentAt { get; set; }
    public DateTimeOffset? TasksSentAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<PracticeTask> Tasks { get; set; } = [];
}

public enum TaskLanguage
{
    CSharp,
    Sql,
}

/// <summary>Роль задачи в сессии: от неё зависят объём и то, есть ли в условии неоднозначность.</summary>
public enum TaskRole
{
    Main,
    Warmup,
}

public sealed class PracticeTask
{
    public Guid Id { get; set; }
    public Guid? DigestId { get; set; }
    public required string TopicId { get; set; }
    public int OrderInDigest { get; set; }
    public TaskLanguage Language { get; set; }
    public TaskRole Role { get; set; }

    /// <summary>Формат C#-задачи; у SQL-задач не задан.</summary>
    public CSharpCategory? Category { get; set; }

    public required string Title { get; set; }
    public required string Statement { get; set; }

    /// <summary>SQL: CREATE TABLE и пример данных. Общая для обеих SQL-задач сессии.</summary>
    public string? Schema { get; set; }

    public List<string> ExpectedQuestions { get; set; } = [];

    /// <summary>
    /// Ответы интервьюера на возможные уточняющие вопросы («вопрос — ответ»). Генерируются вместе с задачей,
    /// чтобы бот в диалоге отвечал одинаково и не расходился с эталонным решением.
    /// </summary>
    public List<string> Clarifications { get; set; } = [];
    public required string ReferenceSolution { get; set; }
    public required string SolutionExplanation { get; set; }
    public required string Complexity { get; set; }
    public int? TelegramMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? SolutionRevealedAt { get; set; }

    /// <summary>Задача закрыта: нажали «Следующая задача» или «Завершить сессию». После этого сессия идёт дальше.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Нажата «Отправить решение»: следующее сообщение пользователя — решение, а не вопрос.</summary>
    public DateTimeOffset? AwaitingSolutionSince { get; set; }

    public List<TaskAttempt> Attempts { get; set; } = [];
    public List<TaskMessage> Messages { get; set; } = [];
}

public enum TaskMessageKind
{
    /// <summary>Сообщение пользователя в диалоге: вопрос, рассуждение, черновик.</summary>
    Question,

    /// <summary>Ответ бота в диалоге.</summary>
    Answer,

    Hint,

    /// <summary>Сообщение пользователя, отправленное как решение.</summary>
    Solution,

    Review,
}

/// <summary>
/// Переписка по задаче. Нужна дважды: как история для модели и чтобы reply на любое сообщение
/// по задаче (не только на условие) находил эту задачу.
/// </summary>
public sealed class TaskMessage
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public TaskMessageKind Kind { get; set; }
    public required string Text { get; set; }
    public int? TelegramMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TaskAttempt
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public PracticeTask? Task { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public required string Answer { get; set; }
    public bool AskedClarifyingQuestions { get; set; }
    public int Score { get; set; }
    public int HintsUsed { get; set; }
    public required string ReviewHtml { get; set; }
}

public sealed class DayLog
{
    public DateOnly Date { get; set; }
    public DayKind Kind { get; set; }
}
