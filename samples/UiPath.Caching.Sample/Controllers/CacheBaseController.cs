using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UiPath.Caching;

namespace UiPath.Caching.Sample.Controllers;

public abstract class CacheBaseController(ICache cache) : ControllerBase
{
    protected ICache Cache { get; } = cache;

    [HttpGet]
    [Route("Get")]
    public async Task<string?> GetAsync(string cacheKey, CancellationToken token) =>
        await Cache.GetAsync<string>(cacheKey, policy: null, token: token);

    [HttpPost]
    [Route("MGet")]
    public async Task<Dictionary<string, string?>> GetAsync([FromBody]string[] keys, CancellationToken token) =>
        (await Cache.GetAsync<string>(keys.Select(s => (CacheKey)s).ToArray(), policy: null, token: token)).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);

    [HttpPost]
    [Route("Set")]
    public async Task<bool> SetAsync([FromQuery] string cacheKey, [FromBody] string value, CancellationToken token) =>
        await Cache.SetAsync(cacheKey, value, policy: null, token: token);

    [HttpPost]
    [Route("MSet")]
    public async Task<bool> MSetAsync([FromBody] Dictionary<string, string?> values, CancellationToken token) =>
        await Cache.SetAsync(values.Select(kv => new KeyValuePair<CacheKey, string?>(kv.Key, kv.Value)).ToArray(), policy: null, token: token);

    [HttpPost]
    [Route("Refresh")]
    public async Task RefreshAsync([FromQuery] string cacheKey, [FromBody] string timespan, CancellationToken token) =>
        await Cache.RefreshAsync<string>(cacheKey, TimeSpan.Parse(timespan, CultureInfo.InvariantCulture), policy: null, token: token);

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
}
