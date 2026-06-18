using Microsoft.AspNetCore.Mvc;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Sample.AspNetCore.Controllers;

[ApiController]
[Route("[controller]")]
public class RedisConnectionController(IRedisConnector redis) : ControllerBase
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
