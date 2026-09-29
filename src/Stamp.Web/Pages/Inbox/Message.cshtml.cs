using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Stamp.Application.Common;
using Stamp.Application.Messages;
using Stamp.Application.Receivers;
using Stamp.Domain.Common;
using Stamp.Domain.Messages;
using Stamp.Web.Infrastructure;

namespace Stamp.Web.Pages.Inbox;

public sealed class MessageModel(InboxService inbox, BlockListService blocks, TimeProvider time) : PageModel
{
    public MessageDetails Message { get; private set; } = null!;

    public DateTimeOffset Now { get; private set; }

    [BindProperty]
    public string? Reply { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken) ? Page() : NotFound();

    public async Task<IActionResult> OnPostReplyAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await inbox.ReplyAsync(User.ReceiverId(), id, Reply ?? string.Empty, cancellationToken);
        return await CompleteAsync(id, result, nameof(Reply), cancellationToken);
    }

    public async Task<IActionResult> OnPostDeclineAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await inbox.DeclineAsync(User.ReceiverId(), id, cancellationToken);
        return await CompleteAsync(id, result, string.Empty, cancellationToken);
    }

    public async Task<IActionResult> OnPostBlockAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await blocks.BlockSenderOfAsync(User.ReceiverId(), id, cancellationToken);
        if (!result.IsSuccess)
        {
            return NotFound();
        }

        TempData["Flash"] = "Sender blocked. They can't send you more stamps.";
        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> CompleteAsync(Guid id, Result<StampOutcome> result, string field, CancellationToken cancellationToken)
    {
        if (!result.IsSuccess)
        {
            if (result.Error.Code == ErrorCodes.NotFound)
            {
                return NotFound();
            }

            ModelState.AddModelError(field, result.Error.Message);
            return await LoadAsync(id, cancellationToken) ? Page() : NotFound();
        }

        TempData["Flash"] = Describe(result.Value);
        return RedirectToPage(new { id });
    }

    private static string Describe(StampOutcome outcome) => (outcome.Status, outcome.PaymentStatus) switch
    {
        (MessageStatus.Replied, PaymentStatus.Captured) => $"Reply sent. You earned {Money.Format(outcome.EarningsCents, outcome.Currency)}.",
        (MessageStatus.Replied, PaymentStatus.Canceled) => "Reply sent. The sender's card hold had already been released, so this stamp couldn't be collected.",
        (MessageStatus.Replied, _) => "Reply sent. We'll collect the stamp shortly.",
        (MessageStatus.Declined, _) => "Declined. The sender wasn't charged.",
        _ => "Done.",
    };

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var message = await inbox.GetAsync(User.ReceiverId(), id, cancellationToken);
        if (message is null)
        {
            return false;
        }

        Message = message;
        Now = time.GetUtcNow();
        return true;
    }
}
