using Microsoft.AspNetCore.Mvc;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Sample.Controllers;

public abstract class RedisConnectionBaseController(IRedisConnector redis) : ControllerBase
{
    [HttpPost]
    public IActionResult ForceReconnect()
    {
        redis.ForceReconnect();
        return Ok();
    }

    [HttpGet]
    public IActionResult Status()
    {
        if (redis.IsConnected)
        {
            return Ok();
        }
        else
        {
            return StatusCode(424);
        }
    }
}

[ApiController]
[Route("[controller]")]
public class RedisConnectionController(IRedisConnector redis) : RedisConnectionBaseController(redis)
{
}

[ApiController]
[Route("[controller]")]
public class SecondaryRedisConnectionController([FromKeyedServices(SecondaryCaching.Key)] IRedisConnector redis)
    : RedisConnectionBaseController(redis)
{
}
