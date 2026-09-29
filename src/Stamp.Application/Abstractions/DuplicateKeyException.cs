namespace Stamp.Application.Abstractions;

/// <summary>A save violated a unique index (e.g. a handle or webhook event id that already exists).</summary>
public sealed class DuplicateKeyException(string message, Exception innerException) : Exception(message, innerException);
