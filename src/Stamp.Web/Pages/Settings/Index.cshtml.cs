using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Stamp.Application.Common;
using Stamp.Application.Receivers;
using Stamp.Domain.Receivers;
using Stamp.Web.Infrastructure;

namespace Stamp.Web.Pages.Settings;

public sealed class IndexModel(ReceiverSettingsService settings, AppUrls urls, IOptions<StampOptions> stampOptions) : PageModel
{
    [BindProperty]
    public SettingsInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public bool Welcome { get; set; }

    public ReceiverSettings Current { get; private set; } = null!;

    public string PublicUrl => urls.PublicPage(Current.Handle ?? string.Empty);

    /// <summary>"localhost:5001/" or "stamp.example.com/" in front of the handle input.</summary>
    public string PublicUrlPrefix => new Uri(urls.PublicPage("x")).Authority + "/";

    public decimal FeePercent => stampOptions.Value.PlatformFeePercent;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return Challenge();
        }

        Input = SettingsInput.From(Current);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await settings.UpdateAsync(User.ReceiverId(), Input.ToCommand(), cancellationToken);
        if (!result.IsSuccess)
        {
            ModelState.AddModelError(FieldFor(result.Error.Target), result.Error.Message);
            return Page();
        }

        TempData["Flash"] = result.Value.PayoutsEnabled
            ? "Saved."
            : "Saved. Next, set up payouts so your page can accept stamps.";
        return RedirectToPage();
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync(User.ReceiverId(), cancellationToken);
        if (current is null)
        {
            return false;
        }

        Current = current;
        return true;
    }

    private static string FieldFor(string? target) => target switch
    {
        nameof(Receiver.Handle) => "Input.Handle",
        nameof(Receiver.DisplayName) => "Input.DisplayName",
        nameof(Receiver.Bio) => "Input.Bio",
        nameof(Receiver.PhotoUrl) => "Input.PhotoUrl",
        nameof(Receiver.StampPriceCents) => "Input.PriceDollars",
        _ => string.Empty,
    };
}

public sealed class SettingsInput
{
    [Required(ErrorMessage = "Pick a handle.")]
    [RegularExpression("^@?[A-Za-z0-9_]{3,30}$", ErrorMessage = "Handles are 3–30 characters: letters, numbers and underscores.")]
    public string Handle { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter your name as senders should see it.")]
    [StringLength(Receiver.MaxDisplayNameLength)]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(Receiver.MaxBioLength)]
    public string? Bio { get; set; }

    [Url(ErrorMessage = "Enter a full https:// link to an image.")]
    [StringLength(Receiver.MaxPhotoUrlLength)]
    public string? PhotoUrl { get; set; }

    [Required(ErrorMessage = "Set a price.")]
    [Range(2, 500, ErrorMessage = "Stamp price must be between $2 and $500.")]
    public decimal? PriceDollars { get; set; }

    public bool DonateToCharity { get; set; }

    public static SettingsInput From(ReceiverSettings settings) => new()
    {
        Handle = settings.Handle ?? string.Empty,
        DisplayName = settings.DisplayName,
        Bio = settings.Bio,
        PhotoUrl = settings.PhotoUrl,
        PriceDollars = settings.StampPriceCents / 100m,
        DonateToCharity = settings.DonateToCharity,
    };

    public UpdateSettingsCommand ToCommand() => new(
        Handle,
        DisplayName,
        Bio,
        PhotoUrl,
        (long)Math.Round((PriceDollars ?? 0) * 100m, MidpointRounding.AwayFromZero),
        DonateToCharity);
}
