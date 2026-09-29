using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Stamp.Application.Receivers;
using Stamp.Web.Infrastructure;

namespace Stamp.Web.Pages.Payouts;

/// <summary>Stripe sends the receiver here when an onboarding link has expired; hand out a fresh one.</summary>
public sealed class RefreshModel(PayoutOnboardingService onboarding) : PageModel
{
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var result = await onboarding.StartAsync(User.ReceiverId(), cancellationToken);
        if (result.IsSuccess)
        {
            return Redirect(result.Value.ToString());
        }

        TempData["Flash"] = result.Error.Message;
        return RedirectToPage("/Payouts/Index");
    }
}
