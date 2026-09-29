using System.Linq.Expressions;
using Stamp.Domain.Messages;

namespace Stamp.Application.Messages;

public static class StampQueries
{
    /// <summary>
    /// SQL-translatable form of <see cref="StampedMessage.NeedsCapture"/> || <see cref="StampedMessage.NeedsCancellation"/>.
    /// </summary>
    public static readonly Expression<Func<StampedMessage, bool>> NeedsSettlement = m =>
        m.PaymentId != null
        && ((m.Status == MessageStatus.Replied && m.PaymentStatus == PaymentStatus.Authorized)
            || ((m.Status == MessageStatus.Declined || m.Status == MessageStatus.Expired || m.Status == MessageStatus.Abandoned)
                && (m.PaymentStatus == PaymentStatus.Created || m.PaymentStatus == PaymentStatus.Authorized)));
}
