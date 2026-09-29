namespace Stamp.Domain.Common;

/// <summary>
/// A business rule was violated. <see cref="Code"/> is stable and safe to branch on;
/// <see cref="Exception.Message"/> is safe to show to the user.
/// </summary>
public sealed class DomainException(string code, string message, string? target = null) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>The property the error refers to, when there is one (e.g. "Handle").</summary>
    public string? Target { get; } = target;
}
