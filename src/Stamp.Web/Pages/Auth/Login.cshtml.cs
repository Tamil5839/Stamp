using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Stamp.Application.Auth;
using Stamp.Application.Common;
using Stamp.Web.Infrastructure;

namespace Stamp.Web.Pages.Auth;

[EnableRateLimiting(RateLimitPolicies.Login)]
public sealed class LoginModel(MagicLinkService magicLinks, IOptions<AuthOptions> authOptions) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(254)]
    public string Email { get; set; } = string.Empty;

    public bool Sent { get; private set; }

    public string LinkLifetime => TimeText.Duration(authOptions.Value.MagicLinkLifetime);

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? RedirectToPage("/Inbox/Index") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await magicLinks.RequestLinkAsync(Email, cancellationToken);
        if (!result.IsSuccess)
        {
            ModelState.AddModelError(nameof(Email), result.Error.Message);
            return Page();
        }

        Sent = true;
        return Page();
    }
}
