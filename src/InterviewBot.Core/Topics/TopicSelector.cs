namespace InterviewBot.Core.Topics;

/// <summary>Снимок темы и прогресса по ней — вход для выбора темы дня.</summary>
public sealed record TopicState(
    string TopicId,
    int Priority,
    int SortOrder,
    TopicStatus Status,
    int Stage,
    DateOnly? NextReviewOn,
    DateOnly? LastShownOn,
    DateOnly StatusChangedOn);

public static class TopicSelector
{
    /// <summary>
    /// Тема для утренней теории. Порядок очереди:
    /// 1) «пробел» — сначала самые давние;
    /// 2) «повторить» и «понял», у которых подошёл срок повторения;
    /// 3) новые — по приоритету и порядку в программе.
    /// Тему, которую показывали вчера или сегодня, пропускаем, если есть альтернатива.
    /// </summary>
    public static string? SelectForTheory(IReadOnlyCollection<TopicState> topics, DateOnly today)
    {
        var fresh = topics.Where(t => !WasShownRecently(t, today)).ToList();

        return SelectByQueue(fresh, today)
            ?? SelectByQueue(topics, today)
            // всё освоено — повторяем то, что видели давнее всего
            ?? topics.OrderBy(t => t.LastShownOn ?? DateOnly.MinValue).ThenBy(t => t.SortOrder).FirstOrDefault()?.TopicId;
    }

    /// <summary>
    /// Тема для задачи-повторения в выходные: то, что уже изучалось, в первую очередь — с подошедшим сроком.
    /// Если изученного нет — обычная очередь.
    /// </summary>
    public static string? SelectForReview(IReadOnlyCollection<TopicState> topics, DateOnly today)
    {
        var studied = topics.Where(t => t.Status is TopicStatus.Understood or TopicStatus.Repeat or TopicStatus.Gap).ToList();

        var due = Due(studied, today).FirstOrDefault();
        if (due is not null)
        {
            return due.TopicId;
        }

        var leastRecent = studied
            .OrderBy(t => t.LastShownOn ?? DateOnly.MinValue)
            .ThenBy(t => t.Priority)
            .FirstOrDefault();

        return leastRecent?.TopicId ?? SelectForTheory(topics, today);
    }

    private static string? SelectByQueue(IReadOnlyCollection<TopicState> topics, DateOnly today)
    {
        var gap = topics
            .Where(t => t.Status == TopicStatus.Gap)
            .OrderBy(t => t.StatusChangedOn)
            .ThenBy(t => t.Priority)
            .ThenBy(t => t.SortOrder)
            .FirstOrDefault();

        var candidate = gap
            ?? Due(topics, today).FirstOrDefault()
            ?? topics
                .Where(t => t.Status == TopicStatus.New)
                .OrderBy(t => t.Priority)
                .ThenBy(t => t.SortOrder)
                .FirstOrDefault();

        return candidate?.TopicId;
    }

    private static IEnumerable<TopicState> Due(IEnumerable<TopicState> topics, DateOnly today) =>
        topics
            .Where(t => t.Status is TopicStatus.Repeat or TopicStatus.Understood && t.NextReviewOn <= today)
            .OrderBy(t => t.NextReviewOn)
            .ThenBy(t => t.Priority)
            .ThenBy(t => t.SortOrder);

    private static bool WasShownRecently(TopicState topic, DateOnly today) =>
        topic.LastShownOn is { } shown && shown >= today.AddDays(-1);
}
