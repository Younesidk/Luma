using Luma.Core.Enums;

namespace Luma.Core.Exceptions;

/// <summary>Raised by providers for any failure the UI should present in a friendly way.</summary>
public class ProviderException : Exception
{
    public ProviderException(string providerId, ProviderErrorKind kind, string message, Exception? inner = null, TimeSpan? retryAfter = null)
        : base(message, inner)
    {
        ProviderId = providerId;
        Kind = kind;
        RetryAfter = retryAfter;
    }

    public string ProviderId { get; }
    public ProviderErrorKind Kind { get; }
    public TimeSpan? RetryAfter { get; }
}

public class LumaException : Exception
{
    public LumaException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class DownloadException : LumaException
{
    public DownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class InvalidImageException : LumaException
{
    public InvalidImageException(string message, Exception? inner = null) : base(message, inner) { }
}
