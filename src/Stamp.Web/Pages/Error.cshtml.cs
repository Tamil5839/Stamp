using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Stamp.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? Code { get; set; }

    public string Heading => Code switch
    {
        404 => "We couldn't find that page",
        429 => "Slow down a little",
        _ => "Something went wrong",
    };

    public string Description => Code switch
    {
        404 => "The link may be mistyped, or the page may no longer exist.",
        429 => "There have been too many attempts from your network. Please wait a few minutes and try again.",
        _ => "Please try again in a moment.",
    };
}
