using InterviewBot.Core.Delivery;
using InterviewBot.Infrastructure.Common;
using InterviewBot.Infrastructure.Persistence;
using InterviewBot.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;

namespace InterviewBot.Infrastructure.Digests;

/// <summary>
/// Атомарный захват через условный UPDATE:
/// <c>UPDATE digests SET theory_sent_at = now WHERE id = @id AND theory_sent_at IS NULL</c>.
/// Postgres блокирует строку на время UPDATE, второй конкурентный запрос после коммита первого
/// перепроверит WHERE и обновит 0 строк — значит, часть уже чья-то.
/// </summary>
internal sealed class DbDeliveryClaimStore(BotDbContext db, BotClock clock) : IDeliveryClaimStore
{
    public async Task<bool> TryClaimAsync(Guid digestId, DigestPart part, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var digest = db.Digests.Where(d => d.Id == digestId);

        var updated = part switch
        {
            DigestPart.Theory => await digest.Where(d => d.TheorySentAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.TheorySentAt, now), cancellationToken),
            DigestPart.Snippet => await digest.Where(d => d.SnippetSentAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.SnippetSentAt, now), cancellationToken),
            DigestPart.Tasks => await digest.Where(d => d.TasksSentAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.TasksSentAt, now), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, null),
        };

        return updated == 1;
    }

    public Task ReleaseAsync(Guid digestId, DigestPart part, CancellationToken cancellationToken)
    {
        var digest = db.Digests.Where(d => d.Id == digestId);

        return part switch
        {
            DigestPart.Theory => digest.ExecuteUpdateAsync(s => s.SetProperty(d => d.TheorySentAt, (DateTimeOffset?)null), cancellationToken),
            DigestPart.Snippet => digest.ExecuteUpdateAsync(s => s.SetProperty(d => d.SnippetSentAt, (DateTimeOffset?)null), cancellationToken),
            DigestPart.Tasks => digest.ExecuteUpdateAsync(s => s.SetProperty(d => d.TasksSentAt, (DateTimeOffset?)null), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, null),
        };
    }
}

internal sealed class TelegramDigestPartSender(
    BotMessenger messenger,
    TaskSender taskSender,
    Digest digest,
    long chatId) : IDigestPartSender
{
    public async Task SendAsync(Guid digestId, DigestPart part, CancellationToken cancellationToken)
    {
        switch (part)
        {
            case DigestPart.Theory:
                await messenger.SendHtmlAsync(chatId, digest.TheoryHtml!, MessageRenderer.FeedbackKeyboard(digest.Id), cancellationToken);
                break;

            case DigestPart.Snippet:
                await messenger.SendHtmlAsync(chatId, digest.SnippetHtml!, null, cancellationToken);
                break;

            case DigestPart.Tasks:
                // только первая задача: остальные приходят по одной, когда закрыта предыдущая (PracticeService.ContinueSessionAsync)
                if (digest.Tasks.OrderBy(t => t.OrderInDigest).FirstOrDefault() is { } first)
                {
                    await taskSender.SendAsync(chatId, first, digest.Tasks, DigestService.TaskHeader(digest), cancellationToken);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(part), part, null);
        }
    }
}
