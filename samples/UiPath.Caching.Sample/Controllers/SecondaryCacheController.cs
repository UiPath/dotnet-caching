using Microsoft.AspNetCore.Mvc;
using UiPath.Caching;

namespace UiPath.Caching.Sample.Controllers;

// Used like the primary: its ICacheFactory picks the provider from the DefaultCache both stacks read.
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
