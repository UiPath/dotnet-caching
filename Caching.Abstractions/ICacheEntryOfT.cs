namespace UiPath.Platform.Caching;

public interface ICacheEntry<out T> : ICacheEntry
{
#pragma warning disable CS0108 // Member hides inherited member; missing new keyword
    T? Value { get; }
#pragma warning restore CS0108 // Member hides inherited member; missing new keyword
}
