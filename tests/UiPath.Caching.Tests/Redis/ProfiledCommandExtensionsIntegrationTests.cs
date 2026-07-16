using StackExchange.Redis;
using StackExchange.Redis.Profiling;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class ProfiledCommandExtensionsIntegrationTests(RedisContainerFixture fixture)
{
    [Fact]
    public async Task GetStatement_IncludesKey_FromLiveProfiledCommand()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var database = multiplexer.GetDatabase();
        await database.PingAsync();

        var session = new ProfilingSession();
        multiplexer.RegisterProfiler(() => session);

        var key = $"profiled-{Guid.NewGuid():N}";
        await database.StringSetAsync(key, "value");
        await database.StringGetAsync(key);

        var commands = session.FinishProfiling().ToList();

        var keyed = commands.FirstOrDefault(c =>
            (string.Equals(c.Command, "SET", StringComparison.Ordinal) || string.Equals(c.Command, "GET", StringComparison.Ordinal))
            && c.GetStatement().Contains(key, StringComparison.Ordinal));

        keyed.Should().NotBeNull();
        keyed!.GetStatement().Should().NotBe(keyed.GetCommandName());
        keyed.GetStatement().Should().StartWith(keyed.Command).And.Contain(key);
        keyed.GetTarget().Should().NotBeNullOrEmpty().And.Contain(":");
    }
}
