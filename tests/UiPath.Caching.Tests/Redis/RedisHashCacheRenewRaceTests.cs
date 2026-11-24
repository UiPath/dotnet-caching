using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Policies;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.KeyMove;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

// A renew whose key changes type, or goes, between the moment it is built and the moment it commits: it must neither delete nor touch what it finds then.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class RedisHashCacheRenewRaceTests(RedisContainerFixture fixture)
{
    private static readonly string RunTag = Guid.NewGuid().ToString("N")[..8];
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var competitor in new[] { "string", "gone" })
        {
            foreach (var expiry in new[] { "past", "future", "persistent" })
            {
                data.Add(competitor, expiry);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_competing_writer_that_replaces_the_hash_before_the_commit_leaves_the_renew_failed_and_what_it_wrote_untouched(string competitor, string expiry)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var real = mux.GetDatabase();
        var key = $"rr:{RunTag}:{Guid.NewGuid():N}";
        var redisKey = (RedisKey)$"test:hash:{key}";
        await real.HashSetAsync(redisKey, "a", "\"x\"");
        await real.KeyExpireAsync(redisKey, Life);
        var raced = false;
        var database = InterceptingProxy.Wrap(real, (method, args) =>
        {
            if (method.Name != nameof(IDatabase.CreateTransaction))
            {
                return InterceptingProxy.Proceed;
            }

            var transaction = real.CreateTransaction(args[0]);
            return InterceptingProxy.Wrap(transaction, (inner, innerArgs) =>
                inner.Name == nameof(ITransaction.ExecuteAsync) ? CompeteThenExecuteAsync(transaction, innerArgs) : InterceptingProxy.Proceed);
        });

        async Task<bool> CompeteThenExecuteAsync(ITransaction transaction, object?[] args)
        {
            raced = true;
            await real.KeyDeleteAsync(redisKey);
            if (competitor == "string")
            {
                await real.StringSetAsync(redisKey, "not a hash", Life);
            }

            return await transaction.ExecuteAsync((CommandFlags)args[0]!);
        }

        var options = expiry switch
        {
            "past" => new HashCacheEntryOptions(ExpireTime: DateTimeOffset.UtcNow.AddMinutes(-1)),
            "future" => new HashCacheEntryOptions(TimeToLive: TimeSpan.FromHours(2)),
            _ => new HashCacheEntryOptions(ExpireTime: DateTimeOffset.MaxValue),
        };

        var outcome = await Create(database).RenewAsync<string>(key, options with { Metadata = new Dictionary<string, string?> { ["m"] = "v" } }, null, Ct);

        raced.Should().BeTrue();
        outcome.Should().Be(StoreOutcome.Failed);
        if (competitor == "string")
        {
            ((string?)await real.StringGetAsync(redisKey)).Should().Be("not a hash");
            (await real.KeyTimeToLiveAsync(redisKey))!.Value.Should().BeCloseTo(Life, TimeSpan.FromSeconds(30), "the string's lifetime is not touched");
        }
        else
        {
            (await real.KeyExistsAsync(redisKey)).Should().BeFalse("not even the metadata is written");
        }

        await real.KeyDeleteAsync(redisKey);
    }

    private static RedisHashCache Create(IDatabase database)
    {
        var connector = Substitute.For<IRedisConnector>();
        connector.Database.Returns(database);
        connector.Version.Returns(new Version(7, 0));
        connector.IsConnected.Returns(true);
        var pipelines = Substitute.For<IResiliencePipelineProvider>();
        pipelines.Get(ResiliencePipelineNames.Read).Returns(EmptyResiliencePipeline.Instance);
        pipelines.Get(ResiliencePipelineNames.Write).Returns(EmptyResiliencePipeline.Instance);
        var strategy = Substitute.For<IRedisKeyStrategy>();
        strategy.GetRedisKey(Arg.Any<CacheKey>()).Returns(call => (RedisKey)$"test:hash:{call.Arg<CacheKey>()}");
        var strategies = Substitute.For<IRedisKeyStrategyFactory>();
        strategies.Create(Arg.Any<CacheOptions>(), Arg.Any<Type>()).Returns(strategy);
        return new RedisHashCache(
            connector,
            new SystemJsonByteSerializerProxy(),
            pipelines,
            new RecordingTelemetryProvider(),
            new RedisCacheOptions { RedisKeyStrategyFactory = strategies, ReadScriptsEnabled = false },
            new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = true },
            new DefaultCachePolicyFactory(null, null),
            TimeProvider.System,
            NullLogger<RedisHashCache>.Instance);
    }
}
