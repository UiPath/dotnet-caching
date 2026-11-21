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
        RedisKey Quarantine(RedisCacheOptions redisOptions) => Maintainer(options, redisOptions).QuarantineKey("stream1");

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
            RedisKeyFormatRules.IsLiveVersion(after, newName!, ["v3"]).Should().BeTrue();
            RedisKeyFormatRules.IsLiveVersion(after, newName!, ["v2"]).Should().BeFalse("a dead version is counted, not moved");
        }
    }

    [Fact]
    public void An_explicit_format_whose_prefixes_overlap_is_refused_on_every_path()
    {
        // With 'e' as separator, a string named "efoo" and a set named "foo" are both "appeseefoo".
        var overlapping = RedisKeyFormat.Create('e', shardKey: false);
        var options = new CacheOptions { AppShortName = "app" };
        var resolve = () => RedisKeyStrategies.Resolve(new RedisCacheOptions { KeyFormat = overlapping }, options, typeof(RedisCache));
        resolve.Should().Throw<InvalidOperationException>().WithMessage("*keyspace 's'*keyspace 'se'*");
        var direct = () => overlapping.CreateStrategy("app", "se");
        direct.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_explicit_format_refuses_an_app_name_with_braces()
    {
        var resolve = () => RedisKeyStrategies.Resolve(
            new RedisCacheOptions { KeyFormat = RedisKeyFormat.Colon }, new CacheOptions { AppShortName = "my{app}" }, "s");
        resolve.Should().Throw<InvalidOperationException>().WithMessage("*braces*");
    }

    [Fact]
    public void Tagged_library_keys_are_never_movable()
    {
        var format = RedisKeyFormat.Create(':', shardKey: true);
        var options = new CacheOptions { AppShortName = App };
        var redisOptions = new RedisCacheOptions { KeyFormat = format };
        var maintainer = Maintainer(options, redisOptions);

        RedisKeyFormatRules.IsMovable(format, NodeListStore.KeyFor(options, redisOptions), App, "s", ':').Should().BeFalse("node list");
        RedisKeyFormatRules.IsMovable(format, maintainer.LockKey, App, "s", ':').Should().BeFalse("lock");
        RedisKeyFormatRules.IsMovable(format, maintainer.QuarantineKey("myapp:st:{topic}"), App, "h", ':').Should().BeFalse("quarantine");
        RedisKeyFormatRules.IsMovable(format, RedisKeyStrategies.Resolve(redisOptions, options, "s").GetRedisKey("user:42"), App, "s", ':').Should().BeTrue();
    }

    [Fact]
    public void Without_a_key_format_the_library_keys_are_unchanged()
    {
        var options = new CacheOptions { AppShortName = "tst" };
        NodeListStore.KeyFor(options, new RedisCacheOptions()).Should().Be((RedisKey)"tst:s:caching:nodes");
        Maintainer(options, new RedisCacheOptions()).LockKey.Should().Be((RedisKey)"tst:s:redisstreamhealthmaintainer:lock2");
    }

    [Fact]
    public void Formats_are_equal_by_id()
    {
        RedisKeyFormat.Create(':', shardKey: true).Should().Be(RedisKeyFormat.Create(':', shardKey: true));
        RedisKeyFormat.Create(':', shardKey: true).GetHashCode().Should().Be(RedisKeyFormat.Create(':', shardKey: true).GetHashCode());
        RedisKeyFormat.Create(':', shardKey: true).Should().NotBe(RedisKeyFormat.Colon);
        RedisKeyFormat.Derive(':', shardKey: false).Should().Be(RedisKeyFormat.Colon);
    }

    [Fact]
    public void Build_and_TryParse_refuse_an_app_name_with_braces()
    {
        var build = () => RedisKeyFormat.Colon.Build("my{app}", "s", "k");
        build.Should().Throw<ArgumentException>().WithMessage("*braces*");
        var parse = () => RedisKeyFormat.Colon.TryParse("my{app}:s:k", "my{app}", "s", out _);
        parse.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_versioned_name_stays_live_in_both_tag_modes(bool shardKey)
    {
        var format = RedisKeyFormat.Create('$', shardKey);
        foreach (var name in new[] { "v3:key", "v3:{tenant}:key" })
        {
            format.TryParse(format.Build(App, "s", name), App, "s", out var parsed).Should().BeTrue();
            RedisKeyFormatRules.IsLiveVersion(format, parsed!, ["v3"]).Should().BeTrue(name);
            RedisKeyFormatRules.IsLiveVersion(format, parsed!, ["v2"]).Should().BeFalse(name);
        }
    }
    private static RedisStreamHealthMaintainer Maintainer(CacheOptions options, RedisCacheOptions redisOptions)
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
        return maintainer;
    }
}
