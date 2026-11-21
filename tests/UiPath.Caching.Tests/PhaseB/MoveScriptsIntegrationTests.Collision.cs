using StackExchange.Redis;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

public partial class MoveScriptsIntegrationTests
{
    // A hash collision of two in-place keys would give them one fence. The collision cannot be found for an 11 character hash, so it is made by hand.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Two_keys_that_share_a_fence_only_refuse_each_other(bool useScripts, bool otherPublishes)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var first = env.Keys;
        var second = new FencedKeys(env.Fence, env.Make("dst2"), env.Ctl);
        var capture = await env.CaptureAsync("n0");
        var otherCapture = await env.Scripts.CaptureAsync(second, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n0-other", 30_000);
        otherCapture.Token.Should().Be("n0");

        var other = otherPublishes
            ? await env.Scripts.PublishAsync(second, MoveTestEnvironment.Id, "other", env.PublicationFor(otherCapture, "n1"))
            : await env.Scripts.BumpAsync(second, MoveTestEnvironment.Id, "n1", 60_000, BumpOperation.Delete);
        var result = await env.Scripts.PublishAsync(first, MoveTestEnvironment.Id, "v", env.PublicationFor(capture, "n2"));

        other.Should().Be(otherPublishes ? MoveScripts.Published : 0);
        result.Should().Be(MoveScripts.FenceMoved);
        (await env.Db.KeyExistsAsync(first.Data)).Should().BeFalse();
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("n1");
    }
}
