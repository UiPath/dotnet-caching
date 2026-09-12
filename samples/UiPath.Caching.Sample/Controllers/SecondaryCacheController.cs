using Microsoft.AspNetCore.Mvc;
using UiPath.Caching;

namespace UiPath.Caching.Sample.Controllers;

// The secondary stack is used the way the primary is: through its ICacheFactory, which picks the provider
// (InMemoryRedis, Redis, InMemory) from the CachingSecondary section's DefaultCache.
[ApiController]
[Route("[controller]")]
public class SecondaryCacheController([FromKeyedServices(SecondaryCaching.Key)] ICacheFactory cacheFactory)
    : CacheBaseController(cacheFactory.CreateCache())
{
}

[ApiController]
[Route("[controller]")]
public class SecondaryHashCacheController([FromKeyedServices(SecondaryCaching.Key)] ICacheFactory cacheFactory)
    : HashCacheBaseController(cacheFactory.CreateHashCache())
{
}
