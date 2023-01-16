namespace UiPath.Platform.Caching;

public interface ICacheEntry<out T> : ICacheEntry
{
    T? Value { get; }
}
