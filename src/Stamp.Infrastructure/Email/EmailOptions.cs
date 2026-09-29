namespace Stamp.Infrastructure.Email;

public enum EmailProviderKind
{
    /// <summary>Writes emails to files and the log, so magic links work locally. Refused in Production.</summary>
    File,

    /// <summary>Sends through the Resend HTTP API.</summary>
    Resend,
}

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public EmailProviderKind Provider { get; set; } = EmailProviderKind.File;

    /// <summary>The From header, e.g. "Stamp &lt;no-reply@yourdomain.com&gt;". Must be a verified sender for Resend.</summary>
    public string From { get; set; } = "Stamp <no-reply@stamp.local>";

    /// <summary>Where the File provider writes emails; relative paths are under the content root.</summary>
    public string FileDirectory { get; set; } = ".emails";

    /// <summary>re_… API key. Set it with user-secrets or the Email__ResendApiKey environment variable.</summary>
    public string? ResendApiKey { get; set; }

    public OutboxOptions Outbox { get; set; } = new();
}

public sealed class OutboxOptions
{
    /// <summary>Turn off to run without the background dispatcher (tests call <see cref="OutboxProcessor"/> directly).</summary>
    public bool DispatcherEnabled { get; set; } = true;

    /// <summary>Fallback poll; new emails wake the dispatcher immediately.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    public int MaxAttempts { get; set; } = 8;

    /// <summary>How long one dispatcher holds an email while sending it.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
}
