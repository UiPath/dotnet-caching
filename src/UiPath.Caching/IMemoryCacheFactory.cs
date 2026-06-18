namespace UiPath.Caching;
public interface IMemoryCacheFactory
{
    IMemoryCache Get(IMemoryCacheOptions memoryOptions);
}
