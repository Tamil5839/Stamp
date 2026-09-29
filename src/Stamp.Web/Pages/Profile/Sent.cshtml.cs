using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Stamp.Application.Messages;
using Stamp.Domain.Messages;
using Stamp.Domain.Receivers;

namespace Stamp.Web.Pages.Profile;

/// <summary>
/// Where the browser lands after the card step (Stripe appends payment_intent and redirect_status).
/// Confirms the authorization with the provider directly, so it doesn't depend on the webhook.
/// </summary>
public sealed class SentModel(SendStampService sending) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "m")]
    public Guid MessageId { get; set; }

    [BindProperty(SupportsGet = true, Name = "redirect_status")]
    public string? RedirectStatus { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Tries { get; set; }

    public SentStampView Stamp { get; private set; } = null!;

    public bool PaymentFailed => string.Equals(RedirectStatus, "failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Poll a few times while the provider finishes authorizing, then stop.</summary>
    public bool ShouldRefresh => Stamp.Status == MessageStatus.AwaitingPayment && !PaymentFailed && Tries < MaxRefreshes;

    public string RefreshUrl => $"/{Stamp.ReceiverHandle}/sent?m={MessageId}&tries={Tries + 1}";

    private const int MaxRefreshes = 10;

    public async Task<IActionResult> OnGetAsync(string handle, CancellationToken cancellationToken)
    {
        if (MessageId == Guid.Empty)
        {
            return NotFound();
        }

        var result = await sending.ConfirmAsync(MessageId, cancellationToken);
        if (!result.IsSuccess || result.Value.ReceiverHandle != HandleRules.Normalize(handle))
        {
            return NotFound();
        }

        Stamp = result.Value;
        return Page();
    }
}
