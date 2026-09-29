using System.Diagnostics.CodeAnalysis;
using Stamp.Domain.Common;

namespace Stamp.Application.Common;

/// <summary>An expected failure a use case reports to its caller. <see cref="Message"/> is user-facing.</summary>
public sealed record Error(string Code, string Message, string? Target = null)
{
    public static Error NotFound(string message = "We couldn't find that.") => new(ErrorCodes.NotFound, message);

    public static Error Conflict(string message) => new(ErrorCodes.Conflict, message);

    public static Error FromDomain(DomainException exception) => new(exception.Code, exception.Message, exception.Target);
}

public static class ErrorCodes
{
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string RateLimited = "rate_limited";
    public const string SenderBlocked = "sender_blocked";
    public const string NotAcceptingStamps = "receiver_not_accepting";
    public const string PaymentUnavailable = "payment_unavailable";
    public const string HandleTaken = "handle_taken";
    public const string LoginLinkInvalid = "login_link_invalid";
}

public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value)
        : base(null) => _value = value;

    private Result(Error error)
        : base(error)
    {
    }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"No value: {Error.Code}");

    public static Result<T> Success(T value) => new(value);

    public static new Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
