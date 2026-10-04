using InterviewBot.Core.Topics;

namespace InterviewBot.Core.Tests;

public class SpacedRepetitionTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    [TestCase(0, 1, 3)]
    [TestCase(1, 2, 7)]
    [TestCase(2, 3, 21)]
    public void Understood_MovesToNextStageWithGrowingInterval(int stage, int expectedStage, int expectedDays)
    {
        var result = SpacedRepetition.Apply(stage, TopicFeedback.Understood, Today);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TopicStatus.Understood));
            Assert.That(result.Stage, Is.EqualTo(expectedStage));
            Assert.That(result.NextReviewOn, Is.EqualTo(Today.AddDays(expectedDays)));
            Assert.That(result.IsMastered, Is.False);
        });
    }

    [Test]
    public void Understood_AfterLastInterval_TopicIsMastered()
    {
        var result = SpacedRepetition.Apply(3, TopicFeedback.Understood, Today);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsMastered, Is.True);
            Assert.That(result.NextReviewOn, Is.Null);
        });
    }

    [Test]
    public void Repeat_KeepsStageAndShowsTomorrow()
    {
        var result = SpacedRepetition.Apply(2, TopicFeedback.Repeat, Today);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TopicStatus.Repeat));
            Assert.That(result.Stage, Is.EqualTo(2));
            Assert.That(result.NextReviewOn, Is.EqualTo(Today.AddDays(1)));
        });
    }

    [TestCase(0)]
    [TestCase(3)]
    public void Gap_ResetsProgress(int stage)
    {
        var result = SpacedRepetition.Apply(stage, TopicFeedback.Gap, Today);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TopicStatus.Gap));
            Assert.That(result.Stage, Is.Zero);
            Assert.That(result.NextReviewOn, Is.Null);
        });
    }

    [Test]
    public void FullCycle_ThreeUnderstood_ThenGap_StartsOver()
    {
        var stage = 0;
        var day = Today;
        foreach (var _ in SpacedRepetition.IntervalsInDays)
        {
            var step = SpacedRepetition.Apply(stage, TopicFeedback.Understood, day);
            stage = step.Stage;
            day = step.NextReviewOn!.Value;
        }

        Assert.That(day, Is.EqualTo(Today.AddDays(3 + 7 + 21)));

        var gap = SpacedRepetition.Apply(stage, TopicFeedback.Gap, day);
        var again = SpacedRepetition.Apply(gap.Stage, TopicFeedback.Understood, day);

        Assert.That(again.NextReviewOn, Is.EqualTo(day.AddDays(3)));
    }

    [Test]
    public void NegativeStage_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpacedRepetition.Apply(-1, TopicFeedback.Understood, Today));
    }
}
