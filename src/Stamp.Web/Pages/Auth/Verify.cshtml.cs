using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Stamp.Application.Auth;

namespace Stamp.Web.Pages.Auth;

/// <summary>
/// The magic link lands here. GET only checks the token; signing in needs a POST, so link-scanning
/// email security tools (which follow GET links) can't use up the one-time token.
/// </summary>
public sealed class VerifyModel(MagicLinkService magicLinks) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public bool Usable { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Usable = await magicLinks.IsUsableAsync(Token, cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var result = await magicLinks.RedeemAsync(Token, cancellationToken);
        if (!result.IsSuccess)
        {
            Usable = false;
            return Page();
        }

        var receiver = result.Value;
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, receiver.ReceiverId.ToString()),
                new Claim(ClaimTypes.Email, receiver.Email),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return receiver.HasProfile
            ? RedirectToPage("/Inbox/Index")
            : RedirectToPage("/Settings/Index", new { welcome = true });
    }
}
