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

/// <summary>The video can't be imported or played. The message is written for the user.</summary>
public sealed class VideoNotPlayableException : LumaException
{
    public VideoNotPlayableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The desktop host could not show the video (no desktop, another instance, graphics failure...).</summary>
public sealed class VideoHostException : LumaException
{
    public VideoHostException(string message, bool recoverable = true, Exception? inner = null) : base(message, inner)
    {
        Recoverable = recoverable;
    }

    public bool Recoverable { get; }
}

/// <summary>
/// Another Luma process is already showing a live wallpaper. Not a failure of this video: callers must not forget or
/// change the shared selection because of it.
/// </summary>
public sealed class VideoHostBusyException : LumaException
{
    public VideoHostBusyException(string message) : base(message) { }
}
