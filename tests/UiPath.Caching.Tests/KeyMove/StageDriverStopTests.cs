using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StageDriverStopTests(RedisContainerFixture fixture)
{
    [Fact]
    public async Task Stop_completes_when_a_step_fails_on_a_connection_disposed_after_stop_began()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        using var entered = new ManualResetEventSlim();
        using var stopBegan = new ManualResetEventSlim();
        kit.LeaseCheck = () =>
        {
            entered.Set();
            stopBegan.Wait(TestContext.Current.CancellationToken);
            throw new ObjectDisposedException("ConnectionMultiplexer");
        };

        kit.Driver.Start([kit.Group], TestContext.Current.CancellationToken);
        entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue("the loop runs its first step at once");
        var stop = kit.Driver.StopAsync();
        stopBegan.Set();

        await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        kit.Alerts.Should().NotContain(a => a.Code == "step_failed", "a fault after stop began is the shutdown, not a failed step");
    }
}
