using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Stamp.Application.Common;
using Stamp.Application.Receivers;
using Stamp.Web.Infrastructure;

namespace Stamp.Web.Pages.Payouts;

public sealed class IndexModel(
    ReceiverSettingsService settings,
    PayoutOnboardingService onboarding,
    IOptions<StampOptions> stampOptions) : PageModel
{
    public ReceiverSettings Current { get; private set; } = null!;

    public decimal FeePercent => stampOptions.Value.PlatformFeePercent;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync(User.ReceiverId(), cancellationToken);
        if (current is null)
        {
            return Challenge();
        }

        Current = current;
        return Page();
    }

    /// <summary>Creates the Express account on first use and sends the receiver to Stripe's hosted onboarding.</summary>
    public async Task<IActionResult> OnPostStartAsync(CancellationToken cancellationToken)
    {
        var result = await onboarding.StartAsync(User.ReceiverId(), cancellationToken);
        if (!result.IsSuccess)
        {
            TempData["Flash"] = result.Error.Message;
            return RedirectToPage();
        }

        return Redirect(result.Value.ToString());
    }
}
