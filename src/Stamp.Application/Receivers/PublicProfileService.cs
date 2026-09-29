using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Domain.Receivers;

namespace Stamp.Application.Receivers;

public sealed record PublicProfile(
    string Handle,
    string DisplayName,
    string Bio,
    string? PhotoUrl,
    long StampPriceCents,
    string Currency,
    bool IsAcceptingStamps,
    TimeSpan ReplyWindow);

public sealed class PublicProfileService(IStampDbContext db, IOptions<StampOptions> options)
{
    public async Task<PublicProfile?> GetAsync(string handle, CancellationToken cancellationToken)
    {
        var normalized = HandleRules.Normalize(handle);
        var receiver = await db.Receivers.AsNoTracking().SingleOrDefaultAsync(r => r.Handle == normalized, cancellationToken);

        return receiver is null
            ? null
            : new PublicProfile(
                receiver.Handle!,
                receiver.DisplayName,
                receiver.Bio,
                receiver.PhotoUrl,
                receiver.StampPriceCents,
                receiver.Currency,
                receiver.IsAcceptingStamps,
                options.Value.ReplyWindow);
    }
}
