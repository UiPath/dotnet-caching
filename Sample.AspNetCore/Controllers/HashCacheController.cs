using Microsoft.AspNetCore.Mvc;
using UiPath.Platform.Caching;
using UiPath.Platform.Sample.AspNetCore.Models;

namespace UiPath.Platform.Sample.AspNetCore.Controllers;
[ApiController]
[Route("[controller]")]
public class HashCacheController(ICacheFactory cacheFactory)
    : HashCacheBaseController(cacheFactory.CreateHashCache())
{
}

[ApiController]
[Route("[controller]")]
public class ResourceDefaultHashCacheController(IHashCache<SampleResource> cache)
    : HashCacheBaseController<SampleResource>(cache)
{
}

[ApiController]
[Route("[controller]")]
public class IntDefaultHashCacheController(ICacheFactory cacheFactory)
    : HashCacheBaseController<int?>(new HashCache<int?>(cacheFactory.CreateHashCache(), new PrefixCacheKeyStrategy("i")))
{
}

[ApiController]
[Route("[controller]")]
public class BoolDefaultHashCacheController(ICacheFactory cacheFactory)
    : HashCacheBaseController<bool?>(new HashCache<bool?>(cacheFactory.CreateHashCache(KnownCacheProviderNames.InMemoryRedis), new PrefixCacheKeyStrategy("b")))
{
}
