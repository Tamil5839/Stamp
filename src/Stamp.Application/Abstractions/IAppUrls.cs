namespace Stamp.Application.Abstractions;

/// <summary>Absolute links for emails and provider redirects. Implemented by the web layer.</summary>
public interface IAppUrls
{
    string MagicLink(string token);

    string Inbox();

    string InboxMessage(Guid messageId);

    string PublicPage(string handle);

    string PayoutsReturn();

    string PayoutsRefresh();
}
