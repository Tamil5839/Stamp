using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stamp.Application.Emails;

namespace Stamp.Infrastructure.Email;

/// <summary>
/// Development sender: writes each email to an .html file and logs the plain-text body, so magic
/// links and notifications can be followed without an email account.
/// </summary>
public sealed partial class FileEmailSender(
    IOptions<EmailOptions> options,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<FileEmailSender> logger) : IEmailSender
{
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex UnsafeFileNameChars();

    public string Directory => Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.FileDirectory));

    public async Task SendAsync(EmailMessage message, string idempotencyKey, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var recipient = UnsafeFileNameChars().Replace(message.To.ToLowerInvariant(), "-").Trim('-');
        var key = idempotencyKey.Length > 8 ? idempotencyKey[..8] : idempotencyKey;
        var path = Path.Combine(Directory, $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{recipient}-{key}.html");

        var document = $"""
            <!DOCTYPE html>
            <meta charset="utf-8">
            <title>{WebUtility.HtmlEncode(message.Subject)}</title>
            <p style="font-family:monospace">To: {WebUtility.HtmlEncode(message.To)}<br>Subject: {WebUtility.HtmlEncode(message.Subject)}</p>
            <hr>
            {message.HtmlBody}
            """;
        await File.WriteAllTextAsync(path, document, cancellationToken);

        logger.LogInformation(
            "Email to {To}: {Subject}\n{TextBody}\n(saved to {Path})", message.To, message.Subject, message.TextBody, path);
    }
}
