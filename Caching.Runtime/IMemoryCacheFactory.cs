namespace UiPath.Platform.Caching;
public interface IMemoryCacheFactory
{
    IMemoryCache Get(IMemoryCacheOptions memoryOptions);
}
