namespace InterviewBot.Core.Topics;

/// <summary>Результат применения отметки к теме.</summary>
/// <param name="Stage">Сколько раз подряд тема была отмечена «Понял». 0 — ещё ни разу или после «Пробела».</param>
/// <param name="NextReviewOn">Когда тему показать снова; null — без расписания (пробел всегда в очереди, освоенная — не показывать).</param>
public sealed record SpacedRepetitionResult(TopicStatus Status, int Stage, DateOnly? NextReviewOn)
{
    public bool IsMastered => Status == TopicStatus.Understood && NextReviewOn is null;
}

/// <summary>
/// Интервальное повторение (SRS, spaced repetition system).
/// Каждое «Понял» отодвигает следующий показ всё дальше: 3 → 7 → 21 день, после чего тема считается освоенной.
/// «Повторить» — показать завтра, этап не теряется. «Пробел» — сброс в начало, тема снова в приоритете.
/// </summary>
public static class SpacedRepetition
{
    public static IReadOnlyList<int> IntervalsInDays { get; } = [3, 7, 21];

    public const int RepeatDelayDays = 1;

    public static SpacedRepetitionResult Apply(int currentStage, TopicFeedback feedback, DateOnly today)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentStage);

        return feedback switch
        {
            TopicFeedback.Understood => ApplyUnderstood(currentStage, today),
            TopicFeedback.Repeat => new SpacedRepetitionResult(TopicStatus.Repeat, currentStage, today.AddDays(RepeatDelayDays)),
            TopicFeedback.Gap => new SpacedRepetitionResult(TopicStatus.Gap, 0, null),
            _ => throw new ArgumentOutOfRangeException(nameof(feedback), feedback, null),
        };
    }

    private static SpacedRepetitionResult ApplyUnderstood(int currentStage, DateOnly today)
    {
        var nextStage = currentStage + 1;

        // stage 1..3 — повторения по интервалам; stage 4 — освоено, больше не показываем
        if (nextStage > IntervalsInDays.Count)
        {
            return new SpacedRepetitionResult(TopicStatus.Understood, IntervalsInDays.Count + 1, null);
        }

        return new SpacedRepetitionResult(TopicStatus.Understood, nextStage, today.AddDays(IntervalsInDays[nextStage - 1]));
    }
}
