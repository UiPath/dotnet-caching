namespace UiPath.Platform.Caching;

public interface ICacheEntrySizeProvider
{
    long GetSize(ICacheEntry entry) => 1;
}
