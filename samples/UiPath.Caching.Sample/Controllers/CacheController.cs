using Microsoft.AspNetCore.Mvc;
using UiPath.Caching;

namespace UiPath.Caching.Sample.Controllers;

[ApiController]
[Route("[controller]")]
public class CacheController(ICacheFactory cacheFactory)
    : CacheBaseController(cacheFactory.CreateCache())
{
}


[ApiController]
[Route("[controller]")]
public class IntDefaultCacheController(ICacheFactory cacheFactory)
    : CacheBaseController<int?>(new Cache<int?>(cacheFactory.CreateCache(), new PrefixCacheKeyStrategy("i")))
{
}

[ApiController]
[Route("[controller]")]
public class BoolDefaultCacheController(ICacheFactory cacheFactory)
    : CacheBaseController<bool?>(new Cache<bool?>(cacheFactory.CreateCache(), new PrefixCacheKeyStrategy("b")))
{
}
