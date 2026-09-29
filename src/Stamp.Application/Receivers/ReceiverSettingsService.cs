using Microsoft.EntityFrameworkCore;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Domain.Common;
using Stamp.Domain.Receivers;

namespace Stamp.Application.Receivers;

public sealed record ReceiverSettings(
    Guid ReceiverId,
    string Email,
    string? Handle,
    string DisplayName,
    string Bio,
    string? PhotoUrl,
    long StampPriceCents,
    string Currency,
    bool DonateToCharity,
    bool PayoutAccountConnected,
    bool PayoutsEnabled,
    bool IsAcceptingStamps);

public sealed record UpdateSettingsCommand(
    string Handle,
    string DisplayName,
    string? Bio,
    string? PhotoUrl,
    long StampPriceCents,
    bool DonateToCharity);

public sealed class ReceiverSettingsService(IStampDbContext db, TimeProvider time)
{
    public async Task<ReceiverSettings?> GetAsync(Guid receiverId, CancellationToken cancellationToken)
    {
        var receiver = await db.Receivers.AsNoTracking().SingleOrDefaultAsync(r => r.Id == receiverId, cancellationToken);
        return receiver is null ? null : ToSettings(receiver);
    }

    public async Task<Result<ReceiverSettings>> UpdateAsync(Guid receiverId, UpdateSettingsCommand command, CancellationToken cancellationToken)
    {
        var receiver = await db.Receivers.SingleOrDefaultAsync(r => r.Id == receiverId, cancellationToken);
        if (receiver is null)
        {
            return Error.NotFound();
        }

        try
        {
            receiver.UpdateProfile(
                command.Handle,
                command.DisplayName,
                command.Bio,
                command.PhotoUrl,
                command.StampPriceCents,
                command.DonateToCharity,
                time.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return Error.FromDomain(ex);
        }

        var handleTaken = new Error(ErrorCodes.HandleTaken, "That handle is taken. Try another one.", nameof(Receiver.Handle));
        if (await db.Receivers.AnyAsync(r => r.Handle == receiver.Handle && r.Id != receiverId, cancellationToken))
        {
            return handleTaken;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateKeyException)
        {
            return handleTaken;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("Your settings changed in another tab. Reload and try again.");
        }

        return ToSettings(receiver);
    }

    private static ReceiverSettings ToSettings(Receiver receiver) => new(
        receiver.Id,
        receiver.Email,
        receiver.Handle,
        receiver.DisplayName,
        receiver.Bio,
        receiver.PhotoUrl,
        receiver.StampPriceCents,
        receiver.Currency,
        receiver.DonateToCharity,
        receiver.PayoutAccountId is not null,
        receiver.PayoutsEnabled,
        receiver.IsAcceptingStamps);
}
