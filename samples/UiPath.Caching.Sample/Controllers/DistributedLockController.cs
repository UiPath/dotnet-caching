using Microsoft.AspNetCore.Mvc;
using UiPath.Caching.Locking;

namespace UiPath.Caching.Sample.Controllers;

public abstract class DistributedLockBaseController(IDistributedLock distributedLock) : ControllerBase
{
    /// <summary>Long enough to watch a lock being contended, short of anything Task.Delay would refuse.</summary>
    private const int MaxHoldSeconds = 60;

    /// <summary>Takes the lock, holds it for <paramref name="holdSeconds"/>, releases it. Call it twice in parallel to see the second call refused.</summary>
    [HttpPost]
    [Route("Hold")]
    public async Task<IActionResult> HoldAsync([FromQuery] string key, [FromQuery] int holdSeconds = 5, CancellationToken token = default)
    {
        if (holdSeconds is < 0 or > MaxHoldSeconds)
        {
            // Checked first: either extreme still yields a positive expiry, so the lock would be taken and then dropped.
            return BadRequest($"{nameof(holdSeconds)} must be between 0 and {MaxHoldSeconds}.");
        }

        var hold = TimeSpan.FromSeconds(holdSeconds);
        var lease = await distributedLock.TryAcquireAsync(key, expiry: hold + TimeSpan.FromSeconds(5), token);
        if (lease is null)
        {
            return Conflict($"Lock '{key}' is held elsewhere.");
        }

        await using (lease)
        {
            await Task.Delay(hold, token);
        }

        return Ok();
    }
}

[ApiController]
[Route("[controller]")]
public class DistributedLockController(IDistributedLock distributedLock) : DistributedLockBaseController(distributedLock)
{
}

[ApiController]
[Route("[controller]")]
public class SecondaryDistributedLockController([FromKeyedServices(SecondaryCaching.Key)] IDistributedLock distributedLock)
    : DistributedLockBaseController(distributedLock)
{
}
