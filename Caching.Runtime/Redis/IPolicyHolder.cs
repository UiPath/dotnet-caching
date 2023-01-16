using Polly;

namespace UiPath.Platform.Caching.Redis;

public interface IPolicyHolder
{
    IAsyncPolicy AsyncPolicy { get; }
}
