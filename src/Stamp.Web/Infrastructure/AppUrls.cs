using Microsoft.Extensions.Options;
using Stamp.Application.Abstractions;

namespace Stamp.Web.Infrastructure;

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Public origin used in emails and provider redirects, e.g. https://stamp.example.com.</summary>
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>Absolute links built from <see cref="AppOptions.BaseUrl"/>, since emails are often sent outside a request.</summary>
public sealed class AppUrls(IOptions<AppOptions> options) : IAppUrls
{
    private Uri Base => new(options.Value.BaseUrl.TrimEnd('/') + "/");

    public string MagicLink(string token) => Absolute($"auth/verify?token={Uri.EscapeDataString(token)}");

    public string Inbox() => Absolute("inbox");

    public string InboxMessage(Guid messageId) => Absolute($"inbox/{messageId}");

    public string PublicPage(string handle) => Absolute(Uri.EscapeDataString(handle));

    public string SentPage(string handle, Guid messageId) => Absolute($"{Uri.EscapeDataString(handle)}/sent?m={messageId}");

    public string PayoutsReturn() => Absolute("payouts/return");

    public string PayoutsRefresh() => Absolute("payouts/refresh");

    private string Absolute(string relative) => new Uri(Base, relative).ToString();
}
