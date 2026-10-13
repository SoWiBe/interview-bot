namespace InterviewBot.Core.Schedule;

/// <summary>Снимок задачи в сессии: порядок, отправлена ли, закрыта ли (нажали «дальше» или открыли решение).</summary>
public sealed record SessionTask(int Order, bool Sent, bool Closed);

public enum SessionStepKind
{
    /// <summary>Пора отправить задачу с указанным номером.</summary>
    SendTask,

    /// <summary>Задача отправлена и ещё не закрыта — ждём пользователя.</summary>
    WaitForCurrent,

    /// <summary>Все задачи закрыты.</summary>
    Finished,
}

public sealed record SessionStep(SessionStepKind Kind, int? Order = null);

/// <summary>
/// Сессия идёт строго по одной задаче: следующая отправляется только после закрытия предыдущей.
/// Поэтому в любой момент есть не больше одной «текущей» задачи, и сообщение без reply относится к ней.
/// </summary>
public static class SessionFlow
{
    public static SessionStep Next(IEnumerable<SessionTask> tasks)
    {
        var current = tasks.Where(t => !t.Closed).OrderBy(t => t.Order).FirstOrDefault();
        if (current is null)
        {
            return new SessionStep(SessionStepKind.Finished);
        }

        return new SessionStep(current.Sent ? SessionStepKind.WaitForCurrent : SessionStepKind.SendTask, current.Order);
    }
}
