using System.Collections.Concurrent;
using InterviewBot.Core.Delivery;

namespace InterviewBot.Core.Tests;

public class DigestDeliveryTests
{
    private static readonly DigestPart[] AllParts = [DigestPart.Theory, DigestPart.Snippet, DigestPart.Tasks];

    /// <summary>Атомарный захват так же, как в БД: TryAdd в ConcurrentDictionary — compare-and-set.</summary>
    private sealed class InMemoryClaims : IDeliveryClaimStore
    {
        public ConcurrentDictionary<(Guid, DigestPart), bool> Claimed { get; } = new();

        public Task<bool> TryClaimAsync(Guid digestId, DigestPart part, CancellationToken cancellationToken) =>
            Task.FromResult(Claimed.TryAdd((digestId, part), true));

        public Task ReleaseAsync(Guid digestId, DigestPart part, CancellationToken cancellationToken)
        {
            Claimed.TryRemove((digestId, part), out _);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSender : IDigestPartSender
    {
        public ConcurrentBag<DigestPart> Sent { get; } = [];
        public DigestPart? FailOn { get; set; }

        public async Task SendAsync(Guid digestId, DigestPart part, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (part == FailOn)
            {
                throw new HttpRequestException("Telegram is down");
            }

            Sent.Add(part);
        }
    }

    [Test]
    public async Task SecondRun_SendsNothing()
    {
        var claims = new InMemoryClaims();
        var sender = new RecordingSender();
        var delivery = new DigestDelivery(claims, sender);
        var digestId = Guid.NewGuid();

        await delivery.DeliverAsync(digestId, AllParts, CancellationToken.None);
        var second = await delivery.DeliverAsync(digestId, AllParts, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(sender.Sent, Has.Count.EqualTo(3));
            Assert.That(second.Sent, Is.Empty);
            Assert.That(second.AlreadySent, Is.EquivalentTo(AllParts));
        });
    }

    [Test]
    public async Task ConcurrentRuns_EachPartSentExactlyOnce()
    {
        var claims = new InMemoryClaims();
        var sender = new RecordingSender();
        var delivery = new DigestDelivery(claims, sender);
        var digestId = Guid.NewGuid();

        // имитация: джоб Quartz и /today сработали одновременно
        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => delivery.DeliverAsync(digestId, AllParts, CancellationToken.None))));

        Assert.That(sender.Sent, Is.EquivalentTo(AllParts));
    }

    [Test]
    public async Task FailedPart_IsReleased_AndRetriedOnNextRun()
    {
        var claims = new InMemoryClaims();
        var sender = new RecordingSender { FailOn = DigestPart.Snippet };
        var delivery = new DigestDelivery(claims, sender);
        var digestId = Guid.NewGuid();

        Assert.ThrowsAsync<HttpRequestException>(() => delivery.DeliverAsync(digestId, AllParts, CancellationToken.None));
        Assert.That(claims.Claimed.Keys, Is.EquivalentTo(new[] { (digestId, DigestPart.Theory) }));

        sender.FailOn = null;
        var retry = await delivery.DeliverAsync(digestId, AllParts, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(retry.AlreadySent, Is.EquivalentTo(new[] { DigestPart.Theory }));
            Assert.That(retry.Sent, Is.EquivalentTo(new[] { DigestPart.Snippet, DigestPart.Tasks }));
            Assert.That(sender.Sent.Count(p => p == DigestPart.Theory), Is.EqualTo(1));
        });
    }
}
