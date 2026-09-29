using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Stamp.Application.Receivers;
using Stamp.Web.Infrastructure;

namespace Stamp.Web.Pages.Payouts;

/// <summary>Stripe sends the receiver here after onboarding; we re-read the account to see if it's ready.</summary>
public sealed class ReturnModel(PayoutOnboardingService onboarding) : PageModel
{
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var result = await onboarding.RefreshAsync(User.ReceiverId(), cancellationToken);

        TempData["Flash"] = !result.IsSuccess
            ? result.Error.Message
            : result.Value?.IsReady == true
                ? "Payouts are set up. Your page now accepts stamps."
                : "Stripe still needs a few details before you can accept stamps.";

        return RedirectToPage("/Payouts/Index");
    }
}
