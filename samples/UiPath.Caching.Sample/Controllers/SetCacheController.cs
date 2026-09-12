using Microsoft.AspNetCore.Mvc;
using UiPath.Caching;

namespace UiPath.Caching.Sample.Controllers;

public abstract class SetCacheBaseController(ISetCache cache) : ControllerBase
{
    protected ISetCache Cache { get; } = cache;

    [HttpPost]
    [Route("Add")]
    public async Task<bool> AddAsync([FromQuery] string cacheKey, [FromBody] string item, CancellationToken token) =>
        await Cache.AddAsync(cacheKey, item, policy: null, token: token);

    [HttpGet]
    [Route("Members")]
    public async Task<IReadOnlyCollection<string?>> MembersAsync(string cacheKey, CancellationToken token) =>
        await Cache.MembersAsync<string>(cacheKey, policy: null, token: token);

    [HttpGet]
    [Route("ContainsItem")]
    public async Task<bool> ContainsItemAsync(string cacheKey, string item, CancellationToken token) =>
        await Cache.ContainsItemAsync(cacheKey, item, token);

    [HttpGet]
    [Route("Count")]
    public async Task<long> CountAsync(string cacheKey, CancellationToken token) =>
        await Cache.CountAsync<string>(cacheKey, token);

    [HttpDelete]
    [Route("RemoveItem")]
    public async Task<bool> RemoveItemAsync([FromQuery] string cacheKey, [FromQuery] string item, CancellationToken token) =>
        await Cache.RemoveItemAsync(cacheKey, item, token);

    [HttpDelete]
    [Route("Remove")]
    public async Task<bool> RemoveAsync([FromQuery] string cacheKey, CancellationToken token) =>
        await Cache.RemoveAsync<string>(cacheKey, token);
}

[ApiController]
[Route("[controller]")]
public class SetCacheController(ISetCache cache) : SetCacheBaseController(cache)
{
}

[ApiController]
[Route("[controller]")]
public class SecondarySetCacheController([FromKeyedServices(SecondaryCaching.Key)] ISetCache cache)
    : SetCacheBaseController(cache)
{
}
