using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Stamp.Application.Receivers;
using Stamp.Domain.Receivers;
using Stamp.Infrastructure.Payments;

namespace Stamp.Web.Pages.Profile;

public sealed class PublicModel(
    PublicProfileService profiles,
    IOptions<PaymentsOptions> payments,
    IOptions<StripeOptions> stripe) : PageModel
{
    public PublicProfile Profile { get; private set; } = null!;

    public bool UsesStripe => payments.Value.Provider == PaymentProviderKind.Stripe;

    public string PaymentProvider => payments.Value.Provider.ToString();

    public string PublishableKey => UsesStripe ? stripe.Value.PublishableKey : string.Empty;

    public string Initials => string.Concat(
        Profile.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part => char.ToUpperInvariant(part[0])));

    public async Task<IActionResult> OnGetAsync(string handle, CancellationToken cancellationToken)
    {
        var canonical = HandleRules.Normalize(handle);
        if (canonical != handle)
        {
            return RedirectPermanent($"/{canonical}");
        }

        var profile = await profiles.GetAsync(handle, cancellationToken);
        if (profile is null)
        {
            return NotFound();
        }

        Profile = profile;
        return Page();
    }
}
