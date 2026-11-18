namespace UiPath.Caching.Migration;

/// <summary>The library path could not answer and the source did not either, so the factory was not run.</summary>
public sealed class CacheUnavailableException : Exception
{
    public CacheUnavailableException()
    {
    }

    public CacheUnavailableException(string message)
        : base(message)
    {
    }

    public CacheUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
