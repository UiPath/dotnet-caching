using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UiPath.Platform.Caching;

namespace UiPath.Platform.Sample.AspNetCore.Controllers;

public abstract class CacheBaseController<TResource>(ICache<TResource> cache) : ControllerBase
{
    protected ICache<TResource> Cache { get; } = cache;

    [HttpGet]
    [Route("Get")]
    public async Task<TResource?> GetAsync(string cacheKey, CancellationToken token) =>
        await Cache.GetAsync(cacheKey, token: token);

    [HttpPost]
    [Route("Set")]
    public async Task<bool> SetAsync([FromQuery] string cacheKey, [FromBody] TResource value, CancellationToken token) =>
        await Cache.SetAsync(cacheKey, value, token);

    [HttpPost]
    [Route("Refresh")]
    public async Task RefreshAsync([FromQuery] string cacheKey, [FromBody] string timespan, CancellationToken token) =>
        await Cache.RefreshAsync(cacheKey, TimeSpan.Parse(timespan, CultureInfo.InvariantCulture), token);

    [HttpDelete]
    [Route("Remove")]
    public async Task<bool> RemoveAsync([FromQuery] string cacheKey, CancellationToken token) =>
        await Cache.RemoveAsync(cacheKey, token);

    [HttpGet]
    [Route("Contains")]
    public async Task<bool> ContainsAsync(string cacheKey, CancellationToken token) =>
        await Cache.ContainsAsync(cacheKey, token);

    [HttpGet]
    [Route("TimeToLive")]
    public async Task<TimeSpan?> TimeToLiveAsync(string cacheKey, CancellationToken token) =>
        await Cache.TimeToLiveAsync(cacheKey, token);

    [HttpGet]
    [Route("ExpireTime")]
    public async Task<DateTimeOffset?> ExpireTimeAsync(string cacheKey, CancellationToken token) =>
        await Cache.ExpireTimeAsync(cacheKey, token);
}
