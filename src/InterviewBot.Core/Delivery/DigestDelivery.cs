namespace InterviewBot.Core.Delivery;

/// <summary>Отдельные сообщения утреннего набора. Каждое отправляется и учитывается независимо.</summary>
public enum DigestPart
{
    Theory,
    Snippet,
    Tasks,
}

/// <summary>
/// Хранилище фактов отправки. <see cref="TryClaimAsync"/> обязан быть атомарным:
/// из двух одновременных вызовов для одной части true получает ровно один.
/// </summary>
public interface IDeliveryClaimStore
{
    Task<bool> TryClaimAsync(Guid digestId, DigestPart part, CancellationToken cancellationToken);

    /// <summary>Отпустить захват, если отправка гарантированно не состоялась (Telegram вернул ошибку).</summary>
    Task ReleaseAsync(Guid digestId, DigestPart part, CancellationToken cancellationToken);
}

public interface IDigestPartSender
{
    Task SendAsync(Guid digestId, DigestPart part, CancellationToken cancellationToken);
}

public sealed record DeliveryResult(IReadOnlyList<DigestPart> Sent, IReadOnlyList<DigestPart> AlreadySent);

/// <summary>
/// Идемпотентная отправка набора. Семантика — at-most-once для каждой части:
/// сначала атомарно «захватываем» часть в БД, потом отправляем.
/// <list type="bullet">
/// <item>повторный запуск джоба / перезапуск сервиса / параллельный /today — часть уже захвачена, второй раз не уйдёт;</item>
/// <item>Telegram вернул ошибку — захват отпускаем, следующий запуск попробует снова;</item>
/// <item>процесс упал между захватом и отправкой — часть потеряна (лучше пропустить, чем прислать дважды),
///   её можно запросить вручную через /today.</item>
/// </list>
/// </summary>
public sealed class DigestDelivery(IDeliveryClaimStore claims, IDigestPartSender sender)
{
    public async Task<DeliveryResult> DeliverAsync(Guid digestId, IReadOnlyList<DigestPart> parts, CancellationToken cancellationToken)
    {
        var sent = new List<DigestPart>();
        var alreadySent = new List<DigestPart>();

        foreach (var part in parts)
        {
            if (!await claims.TryClaimAsync(digestId, part, cancellationToken))
            {
                alreadySent.Add(part);
                continue;
            }

            try
            {
                await sender.SendAsync(digestId, part, cancellationToken);
            }
            catch
            {
                // CancellationToken.None: даже при остановке сервиса захват надо отпустить
                await claims.ReleaseAsync(digestId, part, CancellationToken.None);
                throw;
            }

            sent.Add(part);
        }

        return new DeliveryResult(sent, alreadySent);
    }
}
