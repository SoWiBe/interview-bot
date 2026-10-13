using InterviewBot.Core.Schedule;

namespace InterviewBot.Core.Tests;

public class SessionFlowTests
{
    private static SessionTask Task(int order, bool sent = false, bool closed = false) => new(order, sent, closed);

    [Test]
    public void NothingSent_FirstTaskIsSent()
    {
        var step = SessionFlow.Next([Task(1), Task(2), Task(3)]);

        Assert.That(step, Is.EqualTo(new SessionStep(SessionStepKind.SendTask, 1)));
    }

    [Test]
    public void SentButNotClosed_WaitsForIt_AndDoesNotSendNext()
    {
        var step = SessionFlow.Next([Task(1, sent: true), Task(2), Task(3)]);

        Assert.That(step, Is.EqualTo(new SessionStep(SessionStepKind.WaitForCurrent, 1)));
    }

    [Test]
    public void CurrentClosed_NextIsSent()
    {
        var step = SessionFlow.Next([Task(1, sent: true, closed: true), Task(2), Task(3)]);

        Assert.That(step, Is.EqualTo(new SessionStep(SessionStepKind.SendTask, 2)));
    }

    [Test]
    public void OrderIsTakenFromTasks_NotFromListPosition()
    {
        var step = SessionFlow.Next([Task(3), Task(1, sent: true, closed: true), Task(2, sent: true)]);

        Assert.That(step, Is.EqualTo(new SessionStep(SessionStepKind.WaitForCurrent, 2)));
    }

    [Test]
    public void AllClosed_SessionIsFinished()
    {
        var step = SessionFlow.Next([Task(1, sent: true, closed: true), Task(2, sent: true, closed: true)]);

        Assert.That(step.Kind, Is.EqualTo(SessionStepKind.Finished));
    }

    [Test]
    public void NoTasks_SessionIsFinished()
    {
        Assert.That(SessionFlow.Next([]).Kind, Is.EqualTo(SessionStepKind.Finished));
    }
}

public class TaskRotationTests
{
    [Test]
    public void NoHistory_StartsWithFirstCategory()
    {
        Assert.That(TaskRotation.NextCategory([]), Is.EqualTo(CSharpCategory.Algorithms));
    }

    [Test]
    public void NeverUsedCategory_GoesBeforeUsedOnes()
    {
        var recent = new[] { CSharpCategory.Concurrency, CSharpCategory.Algorithms };

        Assert.That(TaskRotation.NextCategory(recent), Is.EqualTo(CSharpCategory.WriteCode));
    }

    [Test]
    public void AllUsed_PicksLeastRecent()
    {
        var recent = new[]
        {
            CSharpCategory.Collections, CSharpCategory.FixBug, CSharpCategory.Algorithms,
            CSharpCategory.WriteCode, CSharpCategory.Concurrency, CSharpCategory.Collections,
        };

        Assert.That(TaskRotation.NextCategory(recent), Is.EqualTo(CSharpCategory.Concurrency));
    }

    [Test]
    public void FiveDaysInARow_NeverRepeatCategory()
    {
        var history = new List<CSharpCategory>();
        for (var day = 0; day < 5; day++)
        {
            history.Insert(0, TaskRotation.NextCategory(history));
        }

        Assert.That(history, Is.Unique);
    }
}
