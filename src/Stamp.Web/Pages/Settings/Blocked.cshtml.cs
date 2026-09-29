using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Stamp.Application.Receivers;
using Stamp.Web.Infrastructure;

namespace Stamp.Web.Pages.Settings;

public sealed class BlockedModel(BlockListService blocks) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Enter an email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string Email { get; set; } = string.Empty;

    public IReadOnlyList<BlockedSenderItem> Blocked { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Blocked = await blocks.ListAsync(User.ReceiverId(), cancellationToken);

    public async Task<IActionResult> OnPostAddAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Blocked = await blocks.ListAsync(User.ReceiverId(), cancellationToken);
            return Page();
        }

        var result = await blocks.BlockAsync(User.ReceiverId(), Email, cancellationToken);
        if (!result.IsSuccess)
        {
            ModelState.AddModelError(nameof(Email), result.Error.Message);
            Blocked = await blocks.ListAsync(User.ReceiverId(), cancellationToken);
            return Page();
        }

        TempData["Flash"] = $"Blocked {Email.Trim()}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string remove, CancellationToken cancellationToken)
    {
        await blocks.UnblockAsync(User.ReceiverId(), remove, cancellationToken);
        TempData["Flash"] = $"Unblocked {remove}.";
        return RedirectToPage();
    }
}
