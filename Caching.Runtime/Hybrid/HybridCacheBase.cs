using System.Net.Mime;
using Microsoft.Extensions.Caching.Memory;
using UiPath.Platform.Caching.Broadcast;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Hybrid;

public abstract class HybridCacheBase : CacheBase, IDisposable
{
    internal static readonly Uri MachineUri = new($"urn:{Environment.MachineName}");


    protected HybridCacheBase(Func<IMemoryCache> memoryCacheAccessor,
        IChangeTokenFactory changeTokenFactory,
        IChannelPublisher channelPublisher,
        IChannelResolver channelResolver,
        IOptions<HybridCacheOptions> optionsAccessor)
        : base(optionsAccessor.Value)
    {
        MemoryCache = memoryCacheAccessor();
        CacheOptions = optionsAccessor.Value;
        ChangeTokenFactory = changeTokenFactory;
        ChannelPublisher = channelPublisher;
        ChannelResolver = channelResolver;
    }

    protected IChangeTokenFactory ChangeTokenFactory { get; private set; }

    protected IChannelPublisher ChannelPublisher { get; private set; }

    protected IMemoryCache MemoryCache { get; private set; }

    protected IChannelResolver ChannelResolver { get; private set; }

    protected HybridCacheOptions CacheOptions { get; private set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            MemoryCache?.Dispose();
        }
    }

    protected virtual CloudEvent GetCloudEvent(ClearCacheEventData eventData) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Type = CacheConstants.ClearCacheEventType,
            Source = CacheOptions.SourceUri ?? MachineUri,
            DataContentType = MediaTypeNames.Application.Json,
            Data = eventData
        };
}
