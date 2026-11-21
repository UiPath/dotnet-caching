using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

public class RedisKeyFormatWiringTests
{
    private const string App = "MyApp";

    [Fact]
    public void Without_a_key_format_the_configured_or_default_factory_is_used()
    {
        var options = new CacheOptions { AppShortName = App };
        RedisKeyStrategies.Resolve(new RedisCacheOptions(), options, typeof(RedisCache)).Should().BeOfType<PrefixRedisKeyStrategy>();
        RedisKeyStrategies.Resolve(new RedisCacheOptions(), options, "se").Should().BeOfType<PrefixRedisKeyStrategy>();

        var custom = Substitute.For<IRedisKeyStrategyFactory>();
        var strategy = Substitute.For<IRedisKeyStrategy>();
        custom.Create(options, typeof(RedisHashCache)).Returns(strategy);
        RedisKeyStrategies.Resolve(new RedisCacheOptions { RedisKeyStrategyFactory = custom }, options, typeof(RedisHashCache))
            .Should().BeSameAs(strategy);
    }

    [Theory]
    [InlineData(typeof(RedisCache), "s")]
    [InlineData(typeof(RedisHashCache), "h")]
    public void A_key_format_joins_every_key_and_leaves_the_separators_alone(Type cacheType, string keyspace)
    {
        var options = new CacheOptions { AppShortName = App };
        var strategy = RedisKeyStrategies.Resolve(new RedisCacheOptions { KeyFormat = RedisKeyFormat.Dollar }, options, cacheType);

        strategy.GetRedisKey("Tenant:Key").Should().Be((RedisKey)$"myapp${keyspace}$tenant:key");
        options.Separator.Should().Be(CacheOptions.KeySeparator);
    }

    [Fact]
    public void A_key_format_with_a_custom_factory_is_refused()
    {
        var redisOptions = new RedisCacheOptions { KeyFormat = RedisKeyFormat.Dollar, RedisKeyStrategyFactory = new DefaultRedisKeyStrategyFactory() };
        var act = () => RedisKeyStrategies.Resolve(redisOptions, new CacheOptions { AppShortName = App }, "s");
        act.Should().Throw<InvalidOperationException>().WithMessage("*KeyFormat*RedisKeyStrategyFactory*");
    }

    [Fact]
    public void T_S2_13_the_node_list_stays_on_the_pinned_format()
    {
        var options = new CacheOptions { AppShortName = App };
        NodeListStore.KeyFor(options, new RedisCacheOptions { KeyFormat = RedisKeyFormat.Dollar })
            .Should().Be((RedisKey)"myapp$s$caching:nodes");
        NodeListStore.KeyFor(options, new RedisCacheOptions { KeyFormat = RedisKeyFormat.Dollar, PinnedKeyFormat = RedisKeyFormat.Colon })
            .Should().Be(NodeListStore.KeyFor(options, new RedisCacheOptions()));
    }

    [Fact]
    public void T_S2_13_the_maintainer_keys_stay_on_the_pinned_format()
    {
        var options = new CacheOptions { AppShortName = App };
        RedisKey Quarantine(RedisCacheOptions redisOptions)
        {
            var maintainer = new RedisStreamHealthMaintainer(
                Substitute.For<IRedisConnector>(),
                new RecordingTelemetryProvider(),
                Options.Create(new RedisStreamsTopicOptions()),
                Options.Create(redisOptions),
                Options.Create(options),
                NullLogger<RedisStreamHealthMaintainer>.Instance,
                TimeProvider.System);
            maintainer.Initialize();
            return maintainer.QuarantineKey("stream1");
        }

        Quarantine(new RedisCacheOptions { KeyFormat = RedisKeyFormat.Dollar }).Should().Be((RedisKey)"myapp$h$caching:meta:stream$stream1$");
        Quarantine(new RedisCacheOptions { KeyFormat = RedisKeyFormat.Dollar, PinnedKeyFormat = RedisKeyFormat.Colon })
            .Should().Be(Quarantine(new RedisCacheOptions()));
    }

    [Theory]
    [InlineData(':')]
    [InlineData('$')]
    public void T_S2_17_an_app_strategy_reading_the_key_separator_keeps_its_names(char separator)
    {
        var options = new CacheOptions { AppShortName = App, Separator = separator };
        var appStrategy = new PrefixCacheKeyStrategy("v3");
        var key = appStrategy.GetCacheKey<string>(new CacheKey("Tenant:Key", CacheKeyCasing.Sensitive));
        var before = RedisKeyFormat.Derive(options);

        foreach (var after in new[] { RedisKeyFormat.Colon, RedisKeyFormat.Dollar })
        {
            var oldKey = RedisKeyStrategies.Resolve(new RedisCacheOptions(), options, typeof(RedisCache)).GetRedisKey(key);
            var newKey = RedisKeyStrategies.Resolve(new RedisCacheOptions { KeyFormat = after }, options, typeof(RedisCache)).GetRedisKey(key);

            before.TryParse(oldKey, App, "s", out var oldName).Should().BeTrue();
            after.TryParse(newKey, App, "s", out var newName).Should().BeTrue();
            newName.Should().Be(oldName).And.Be("v3:Tenant:Key");
            options.Separator.Should().Be(separator);
            RedisKeyFormatRules.IsLiveVersion(newName!, ["v3"]).Should().BeTrue();
            RedisKeyFormatRules.IsLiveVersion(newName!, ["v2"]).Should().BeFalse("a dead version is counted, not moved");
        }
    }
}
