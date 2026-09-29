using Microsoft.EntityFrameworkCore;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Application.Payments;
using Stamp.Domain.Common;
using Stamp.Domain.Messages;

namespace Stamp.Application.Messages;

public enum InboxView
{
    /// <summary>Stamps waiting for a reply, soonest to expire first.</summary>
    Pending,

    /// <summary>Replied, declined and expired stamps, most recent first.</summary>
    History,
}

public sealed record InboxItem(
    Guid Id,
    string SenderName,
    string Subject,
    string Preview,
    long EarningsCents,
    string Currency,
    MessageStatus Status,
    PaymentStatus PaymentStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ResolvedAt);

public sealed record MessageDetails(
    Guid Id,
    string SenderName,
    string SenderEmail,
    string Subject,
    string Body,
    long AmountCents,
    long EarningsCents,
    string Currency,
    MessageStatus Status,
    PaymentStatus PaymentStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ResolvedAt,
    string? ReplyBody,
    bool CanReply,
    bool SenderBlocked);

/// <summary>What happened to the money after a reply or decline was saved.</summary>
public sealed record StampOutcome(MessageStatus Status, PaymentStatus PaymentStatus, long EarningsCents, string Currency);

/// <summary>The receiver's side: list, read, reply to and decline stamped messages.</summary>
public sealed class InboxService(
    IStampDbContext db,
    StampTransitions transitions,
    PaymentSettlement settlement,
    TimeProvider time)
{
    private const int PreviewLength = 140;
    private const int HistoryLimit = 100;

    public async Task<IReadOnlyList<InboxItem>> ListAsync(Guid receiverId, InboxView view, CancellationToken cancellationToken)
    {
        var mine = db.Messages.AsNoTracking().Where(m => m.ReceiverId == receiverId);

        var query = view == InboxView.Pending
            ? mine.Where(m => m.Status == MessageStatus.Pending).OrderBy(m => m.ExpiresAt)
            : mine.Where(m => m.Status == MessageStatus.Replied || m.Status == MessageStatus.Declined || m.Status == MessageStatus.Expired)
                .OrderByDescending(m => m.ResolvedAt)
                .Take(HistoryLimit);

        var messages = await query.ToListAsync(cancellationToken);
        return messages.Select(m => new InboxItem(
                m.Id,
                m.SenderName,
                m.Subject,
                Preview(m.Body),
                m.ReceiverEarningsCents,
                m.Currency,
                m.Status,
                m.PaymentStatus,
                m.CreatedAt,
                m.ExpiresAt,
                m.ResolvedAt))
            .ToList();
    }

    public async Task<MessageDetails?> GetAsync(Guid receiverId, Guid messageId, CancellationToken cancellationToken)
    {
        var message = await FindVisibleAsync(receiverId, messageId, tracked: false, cancellationToken);
        if (message is null)
        {
            return null;
        }

        var blocked = await db.BlockedSenders.AnyAsync(
            b => b.ReceiverId == receiverId && b.Email == message.SenderEmail, cancellationToken);

        return new MessageDetails(
            message.Id,
            message.SenderName,
            message.SenderEmail,
            message.Subject,
            message.Body,
            message.AmountCents,
            message.ReceiverEarningsCents,
            message.Currency,
            message.Status,
            message.PaymentStatus,
            message.CreatedAt,
            message.ExpiresAt,
            message.ResolvedAt,
            message.ReplyBody,
            message.IsReplyWindowOpen(time.GetUtcNow()),
            blocked);
    }

    /// <summary>
    /// Saves the reply and queues it for the sender in one transaction, then captures the stamp.
    /// If the capture fails the reply still stands; the expiry job retries the capture.
    /// </summary>
    public Task<Result<StampOutcome>> ReplyAsync(Guid receiverId, Guid messageId, string reply, CancellationToken cancellationToken) =>
        ResolveAsync(receiverId, messageId, message => transitions.ReplyAsync(message, reply, cancellationToken), cancellationToken);

    /// <summary>Declines the stamp, tells the sender they weren't charged, and releases the hold.</summary>
    public Task<Result<StampOutcome>> DeclineAsync(Guid receiverId, Guid messageId, CancellationToken cancellationToken) =>
        ResolveAsync(receiverId, messageId, message => transitions.DeclineAsync(message, cancellationToken), cancellationToken);

    private async Task<Result<StampOutcome>> ResolveAsync(
        Guid receiverId, Guid messageId, Func<StampedMessage, Task> transition, CancellationToken cancellationToken)
    {
        var message = await FindVisibleAsync(receiverId, messageId, tracked: true, cancellationToken);
        if (message is null)
        {
            return Error.NotFound();
        }

        try
        {
            await transition(message);
        }
        catch (DomainException ex)
        {
            return Error.FromDomain(ex);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("This message changed while you were working on it. Reload the page to see its latest state.");
        }

        await settlement.SettleAsync(message, cancellationToken);
        return new StampOutcome(message.Status, message.PaymentStatus, message.ReceiverEarningsCents, message.Currency);
    }

    /// <summary>Unpaid drafts never reach the receiver.</summary>
    private Task<StampedMessage?> FindVisibleAsync(Guid receiverId, Guid messageId, bool tracked, CancellationToken cancellationToken)
    {
        var messages = tracked ? db.Messages : db.Messages.AsNoTracking();
        return messages.SingleOrDefaultAsync(
            m => m.Id == messageId
                 && m.ReceiverId == receiverId
                 && m.Status != MessageStatus.AwaitingPayment
                 && m.Status != MessageStatus.Abandoned,
            cancellationToken);
    }

    private static string Preview(string body)
    {
        var singleLine = TextRules.SingleLine(body);
        return singleLine.Length <= PreviewLength ? singleLine : singleLine[..(PreviewLength - 1)].TrimEnd() + "…";
    }
}
