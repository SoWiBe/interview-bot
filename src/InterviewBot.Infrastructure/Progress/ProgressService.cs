using InterviewBot.Core.Progress;
using InterviewBot.Core.Topics;
using InterviewBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InterviewBot.Infrastructure.Progress;

public enum FeedbackOutcome
{
    Applied,
    AlreadyGiven,
    NotFound,
}

public sealed record FeedbackResult(FeedbackOutcome Outcome, TopicFeedback? Feedback = null, SpacedRepetitionResult? Repetition = null);

public sealed record StatsReport(
    IReadOnlyDictionary<TopicStatus, int> TopicsByStatus,
    int Mastered,
    int DueToday,
    int Streak,
    int AttemptedTasks,
    int TasksWithQuestions,
    double? AverageScore);

public sealed class ProgressService(BotDbContext db)
{
    public async Task<FeedbackResult> ApplyFeedbackAsync(Guid digestId, TopicFeedback feedback, DateOnly today, CancellationToken cancellationToken)
    {
        var digest = await db.Digests
            .Include(d => d.Topic).ThenInclude(t => t!.Progress)
            .FirstOrDefaultAsync(d => d.Id == digestId, cancellationToken);

        if (digest?.Topic?.Progress is not { } progress)
        {
            return new FeedbackResult(FeedbackOutcome.NotFound);
        }

        // Одна отметка на набор: повторное нажатие не должно второй раз двигать интервал
        if (digest.Feedback is { } given)
        {
            return new FeedbackResult(FeedbackOutcome.AlreadyGiven, given);
        }

        var result = SpacedRepetition.Apply(progress.Stage, feedback, today);
        if (progress.Status != result.Status)
        {
            progress.StatusChangedOn = today;
        }

        progress.Status = result.Status;
        progress.Stage = result.Stage;
        progress.NextReviewOn = result.NextReviewOn;
        digest.Feedback = feedback;

        await db.SaveChangesAsync(cancellationToken);
        await MarkActiveAsync(today, cancellationToken);

        return new FeedbackResult(FeedbackOutcome.Applied, feedback, result);
    }

    /// <summary>Upsert одним запросом: день с активностью перекрывает и «паузу», и отсутствие записи.</summary>
    public Task MarkActiveAsync(DateOnly day, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"INSERT INTO day_logs (date, kind) VALUES ({day}, 'Active') ON CONFLICT (date) DO UPDATE SET kind = 'Active'",
            cancellationToken);

    /// <summary>Пауза на <paramref name="days"/> дней начиная с сегодня; 0 — снять паузу.</summary>
    public async Task<DateOnly?> PauseAsync(UserSettings settings, int days, DateOnly today, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.DayLogs
            .Where(d => d.Kind == DayKind.Paused && d.Date >= today)
            .ExecuteDeleteAsync(cancellationToken);

        settings.PausedUntil = days == 0 ? null : today.AddDays(days - 1);

        for (var day = today; day <= settings.PausedUntil; day = day.AddDays(1))
        {
            // активный день остаётся активным
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO day_logs (date, kind) VALUES ({day}, 'Paused') ON CONFLICT (date) DO NOTHING",
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return settings.PausedUntil;
    }

    public async Task<StatsReport> GetStatsAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var progress = db.TopicProgress.Where(p => db.Topics.Any(t => t.Id == p.TopicId && t.IsActive));

        var byStatus = await progress
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        var mastered = await progress.CountAsync(
            p => p.Status == TopicStatus.Understood && p.NextReviewOn == null && p.Stage > 0, cancellationToken);
        var dueToday = await progress.CountAsync(p => p.NextReviewOn <= today, cancellationToken);

        var days = await db.DayLogs
            .Where(d => d.Date >= today.AddDays(-400))
            .ToDictionaryAsync(d => d.Date, d => d.Kind, cancellationToken);

        // По задаче берём лучшую попытку; «задал вопросы» — хотя бы в одной попытке. Эквивалент SQL:
        //   SELECT count(*), count(*) FILTER (WHERE asked), avg(best_score)
        //   FROM (SELECT task_id, bool_or(asked_clarifying_questions) AS asked, max(score) AS best_score
        //         FROM task_attempts GROUP BY task_id) per_task;
        var perTask = db.TaskAttempts
            .GroupBy(a => a.TaskId)
            .Select(g => new
            {
                Asked = g.Count(a => a.AskedClarifyingQuestions) > 0,
                BestScore = g.Max(a => a.Score),
            });

        var attempted = await perTask.CountAsync(cancellationToken);
        var asked = await perTask.CountAsync(x => x.Asked, cancellationToken);
        var averageScore = attempted > 0 ? await perTask.AverageAsync(x => (double)x.BestScore, cancellationToken) : (double?)null;

        return new StatsReport(
            byStatus,
            mastered,
            dueToday,
            StreakCalculator.Calculate(days, today),
            attempted,
            asked,
            averageScore);
    }
}
