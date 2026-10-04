using InterviewBot.Core.Topics;

namespace InterviewBot.Core.Tests;

public class TopicSelectorTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private int _sortOrder;

    private TopicState Topic(
        string id,
        TopicStatus status = TopicStatus.New,
        int priority = 1,
        DateOnly? nextReview = null,
        DateOnly? lastShown = null,
        DateOnly? statusChanged = null) =>
        new(id, priority, _sortOrder++, status, 0, nextReview, lastShown, statusChanged ?? Today.AddDays(-30));

    [Test]
    public void Gap_GoesBeforeDueReviewAndNew()
    {
        var topics = new[]
        {
            Topic("new"),
            Topic("due", TopicStatus.Understood, nextReview: Today),
            Topic("gap", TopicStatus.Gap),
        };

        Assert.That(TopicSelector.SelectForTheory(topics, Today), Is.EqualTo("gap"));
    }

    [Test]
    public void OldestGapFirst()
    {
        var topics = new[]
        {
            Topic("recent-gap", TopicStatus.Gap, statusChanged: Today.AddDays(-1)),
            Topic("old-gap", TopicStatus.Gap, statusChanged: Today.AddDays(-10)),
        };

        Assert.That(TopicSelector.SelectForTheory(topics, Today), Is.EqualTo("old-gap"));
    }

    [Test]
    public void DueReview_GoesBeforeNew()
    {
        var topics = new[]
        {
            Topic("new"),
            Topic("repeat", TopicStatus.Repeat, nextReview: Today.AddDays(-2)),
        };

        Assert.That(TopicSelector.SelectForTheory(topics, Today), Is.EqualTo("repeat"));
    }

    [Test]
    public void NotYetDueReview_IsSkipped()
    {
        var topics = new[]
        {
            Topic("later", TopicStatus.Understood, nextReview: Today.AddDays(3)),
            Topic("new"),
        };

        Assert.That(TopicSelector.SelectForTheory(topics, Today), Is.EqualTo("new"));
    }

    [Test]
    public void New_ByPriorityThenProgramOrder()
    {
        var topics = new[]
        {
            Topic("p2-first", priority: 2),
            Topic("p1-second"),
            Topic("p1-third"),
        };

        Assert.That(TopicSelector.SelectForTheory(topics, Today), Is.EqualTo("p1-second"));
    }

    [Test]
    public void YesterdaysTopic_IsNotRepeated_WhenThereIsAlternative()
    {
        var topics = new[]
        {
            Topic("gap-yesterday", TopicStatus.Gap, lastShown: Today.AddDays(-1)),
            Topic("new"),
        };

        Assert.That(TopicSelector.SelectForTheory(topics, Today), Is.EqualTo("new"));
    }

    [Test]
    public void YesterdaysTopic_IsRepeated_WhenItIsTheOnlyOption()
    {
        var topics = new[] { Topic("gap-yesterday", TopicStatus.Gap, lastShown: Today.AddDays(-1)) };

        Assert.That(TopicSelector.SelectForTheory(topics, Today), Is.EqualTo("gap-yesterday"));
    }

    [Test]
    public void AllMastered_PicksLeastRecentlyShown()
    {
        var topics = new[]
        {
            Topic("a", TopicStatus.Understood, lastShown: Today.AddDays(-5)),
            Topic("b", TopicStatus.Understood, lastShown: Today.AddDays(-40)),
        };

        Assert.That(TopicSelector.SelectForTheory(topics, Today), Is.EqualTo("b"));
    }

    [Test]
    public void Empty_ReturnsNull()
    {
        Assert.That(TopicSelector.SelectForTheory([], Today), Is.Null);
    }

    [Test]
    public void Review_PrefersDueStudiedTopic()
    {
        var topics = new[]
        {
            Topic("new"),
            Topic("understood-later", TopicStatus.Understood, nextReview: Today.AddDays(5), lastShown: Today.AddDays(-20)),
            Topic("due", TopicStatus.Understood, nextReview: Today.AddDays(-1), lastShown: Today.AddDays(-3)),
        };

        Assert.That(TopicSelector.SelectForReview(topics, Today), Is.EqualTo("due"));
    }

    [Test]
    public void Review_WithoutStudiedTopics_FallsBackToTheoryQueue()
    {
        var topics = new[] { Topic("new") };

        Assert.That(TopicSelector.SelectForReview(topics, Today), Is.EqualTo("new"));
    }
}
