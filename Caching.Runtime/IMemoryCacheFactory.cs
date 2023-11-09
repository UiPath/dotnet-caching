namespace UiPath.Platform.Caching;
public interface IMemoryCacheFactory
{
    IMemoryCache Get(IMemoryStatisticsOptions memoryStatisticsOptions);
}
