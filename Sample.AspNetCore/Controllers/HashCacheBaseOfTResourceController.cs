using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UiPath.Platform.Caching;

namespace UiPath.Platform.Sample.AspNetCore.Controllers;
[ApiController]
public abstract class HashCacheBaseController<TResource>(IHashCache<TResource> cache) : ControllerBase
{
    [HttpGet]
    [Route("GetItem")]
    public async Task<TResource?> GetItemAsync(string cacheKey, string field, CancellationToken token) =>
        await cache.GetItemAsync(cacheKey, field, token);

    [HttpPost]
    [Route("GetItems")]
    public async Task<IDictionary<string, TResource?>> GetItemsAsync(string cacheKey, string[] fields, CancellationToken token) =>
    await cache.GetAsync(cacheKey, fields, token);

    [HttpGet]
    [Route("Get")]
    public async Task<IDictionary<string, TResource?>> GetAsync(string cacheKey, CancellationToken token) =>
        await cache.GetAsync(cacheKey, token);

    [HttpPost]
    [Route("Set")]
    public async Task<bool> SetAsync([FromQuery] string cacheKey, [FromBody] IDictionary<string, TResource?> values, CancellationToken token) =>
        await cache.SetAsync(cacheKey, values, token);

    [HttpPost]
    [Route("SetWithMetadata")]
    public async Task<bool> SetWithMetadataAsync([FromQuery] string cacheKey, [FromBody] ResourceHashEntry<TResource> entry, CancellationToken token) =>
        await cache.SetAsync(cacheKey, entry.Values, new HashCacheEntryOptions(Metadata: entry.Metadata), token);

    [HttpPost]
    [Route("Refresh")]
    public async Task RefreshAsync([FromQuery] string cacheKey, [FromBody] string timespan, CancellationToken token) =>
        await cache.RefreshAsync(cacheKey, TimeSpan.Parse(timespan, CultureInfo.InvariantCulture), token);

    [HttpDelete]
    [Route("Remove")]
    public async Task<bool> RemoveAsync([FromQuery] string cacheKey, CancellationToken token) =>
        await cache.RemoveAsync(cacheKey, token);

    [HttpGet]
    [Route("Contains")]
    public async Task<bool> ContainsAsync(string cacheKey, CancellationToken token) =>
        await cache.ContainsAsync(cacheKey, token);

    [HttpGet]
    [Route("TimeToLive")]
    public async Task<TimeSpan?> TimeToLiveAsync(string cacheKey, CancellationToken token) =>
        await cache.TimeToLiveAsync(cacheKey, token);

    [HttpGet]
    [Route("ExpireTime")]
    public async Task<DateTimeOffset?> ExpireTimeAsync(string cacheKey, CancellationToken token) =>
        await cache.ExpireTimeAsync(cacheKey, token);

    [HttpGet]
    [Route("GetMetadata")]
    public async Task<IDictionary<string, string?>?> GetMetadataAsync(string cacheKey, CancellationToken token) =>
        await cache.GetMetadataAsync(cacheKey, token);

    [HttpPost]
    [Route("SetMetadata")]
    public async Task<bool> SetMetadataAsync([FromQuery] string cacheKey, [FromBody] IDictionary<string, string?> values, CancellationToken token) =>
        await cache.SetMetadataAsync(cacheKey, values, token);
}

public record ResourceHashEntry<TResource>(IDictionary<string, TResource?> Values, IDictionary<string, string?>? Metadata);
