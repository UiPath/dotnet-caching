using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UiPath.Caching;

namespace UiPath.Caching.Sample.Controllers;

[ApiController]
public abstract class HashCacheBaseController(IHashCache cache) : ControllerBase
{
    protected IHashCache Cache { get; } = cache;

    [HttpGet]
    [Route("GetItem")]
    public async Task<string?> GetItemAsync(string cacheKey, string field, CancellationToken token) =>
        await Cache.GetItemAsync<string>(cacheKey, field, policy: null, token: token);

    [HttpPost]
    [Route("GetItems")]
    public async Task<IDictionary<string, string?>> GetItemsAsync(string cacheKey, string[] fields, CancellationToken token) =>
        await Cache.GetAsync<string>(cacheKey, fields, policy: null, token: token);

    [HttpGet]
    [Route("Get")]
    public async Task<IDictionary<string, string?>> GetAsync(string cacheKey, CancellationToken token) =>
        await Cache.GetAsync<string>(cacheKey, policy: null, token: token);

    [HttpPost]
    [Route("Set")]
    public async Task<bool> SetAsync([FromQuery] string cacheKey, [FromBody] IDictionary<string, string?> values, CancellationToken token) =>
        await Cache.SetAsync(cacheKey, values, policy: null, token: token);

    [HttpPost]
    [Route("SetWithMetadata")]
    public async Task<bool> SetWithMetadataAsync([FromQuery] string cacheKey, [FromBody] HashEntry entry, CancellationToken token) =>
        await Cache.SetAsync(cacheKey, entry.Values, new HashCacheEntryOptions(Metadata: entry.Metadata), token: token);

    [HttpPost]
    [Route("Refresh")]
    public async Task<bool> RefreshAsync([FromQuery] string cacheKey, [FromBody] string value, CancellationToken token) =>
        await Cache.RefreshAsync<string>(cacheKey, TimeSpan.Parse(value, CultureInfo.InvariantCulture), policy: null, token: token);

    [HttpDelete]
    [Route("Remove")]
    public async Task<bool> RemoveAsync([FromQuery] string cacheKey, CancellationToken token) =>
        await Cache.RemoveAsync<string>(cacheKey, token);

    [HttpGet]
    [Route("Contains")]
    public async Task<bool> ContainsAsync(string cacheKey, CancellationToken token) =>
        await Cache.ContainsAsync<string>(cacheKey, token);

    [HttpGet]
    [Route("TimeToLive")]
    public async Task<TimeSpan?> TimeToLiveAsync(string cacheKey, CancellationToken token) =>
        await Cache.TimeToLiveAsync<string>(cacheKey, token);

    [HttpGet]
    [Route("ExpireTime")]
    public async Task<DateTimeOffset?> ExpireTimeAsync(string cacheKey, CancellationToken token) =>
        await Cache.ExpireTimeAsync<string>(cacheKey, token);

    [HttpGet]
    [Route("GetMetadata")]
    public async Task<IDictionary<string, string?>?> GetMetadataAsync(string cacheKey, CancellationToken token) =>
        await Cache.GetMetadataAsync<string>(cacheKey, token);

    [HttpPost]
    [Route("SetMetadata")]
    public async Task<bool> SetMetadataAsync([FromQuery] string cacheKey, [FromBody] IDictionary<string, string?> values, CancellationToken token) =>
        await Cache.SetMetadataAsync<string>(cacheKey, values, token);
}

public record HashEntry(IDictionary<string, string?> Values, IDictionary<string, string?>? Metadata);
