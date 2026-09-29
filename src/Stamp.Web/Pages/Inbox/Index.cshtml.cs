using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Stamp.Application.Messages;
using Stamp.Application.Receivers;
using Stamp.Web.Infrastructure;

namespace Stamp.Web.Pages.Inbox;

public sealed class IndexModel(InboxService inbox, ReceiverSettingsService settings, TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? View { get; set; }

    public bool ShowingHistory => string.Equals(View, "history", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<InboxItem> Items { get; private set; } = [];

    public ReceiverSettings Receiver { get; private set; } = null!;

    public DateTimeOffset Now { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var receiverId = User.ReceiverId();
        var receiver = await settings.GetAsync(receiverId, cancellationToken);
        if (receiver is null)
        {
            return Challenge();
        }

        Receiver = receiver;
        Now = time.GetUtcNow();
        Items = await inbox.ListAsync(receiverId, ShowingHistory ? InboxView.History : InboxView.Pending, cancellationToken);
        return Page();
    }
}
