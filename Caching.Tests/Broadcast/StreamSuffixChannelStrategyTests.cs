using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class StreamSuffixChannelStrategyTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void GetRedisChannel_joins_stream_key_and_name_with_options_separator()
    {
        var streamKeyStrategy = _fixture.Freeze<IRedisStreamKeyStrategy>();
        var topicKey = _fixture.Create<TopicKey>();
        streamKeyStrategy.GetRedisKey(topicKey).Returns(new RedisKey("app:st:topicA"));
        var options = new CacheOptions { Separator = ':', AppShortName = "app" };

        var sut = new StreamSuffixChannelStrategy(streamKeyStrategy, options, "notify");

        var channel = sut.GetRedisChannel(topicKey);

        channel.ToString().Should().Be("app:st:topicA:notify");
        channel.IsSharded.Should().BeFalse();
    }

    [Fact]
    public void GetRedisChannel_uses_separator_from_cache_options()
    {
        var streamKeyStrategy = _fixture.Freeze<IRedisStreamKeyStrategy>();
        var topicKey = _fixture.Create<TopicKey>();
        streamKeyStrategy.GetRedisKey(topicKey).Returns(new RedisKey("app$st$topicA"));
        var options = new CacheOptions { Separator = '$', AppShortName = "app" };

        var sut = new StreamSuffixChannelStrategy(streamKeyStrategy, options, "notify");

        var channel = sut.GetRedisChannel(topicKey);

        channel.ToString().Should().Be("app$st$topicA$notify");
    }

    [Fact]
    public void GetRedisChannel_lowercases_name()
    {
        var streamKeyStrategy = _fixture.Freeze<IRedisStreamKeyStrategy>();
        var topicKey = _fixture.Create<TopicKey>();
        streamKeyStrategy.GetRedisKey(topicKey).Returns(new RedisKey("app:st:topicA"));
        var options = new CacheOptions { Separator = ':', AppShortName = "app" };

        var sut = new StreamSuffixChannelStrategy(streamKeyStrategy, options, "Notify");

        var channel = sut.GetRedisChannel(topicKey);

        channel.ToString().Should().Be("app:st:topicA:notify");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetRedisChannel_falls_back_to_default_name_when_not_set(string? name)
    {
        var streamKeyStrategy = _fixture.Freeze<IRedisStreamKeyStrategy>();
        var topicKey = _fixture.Create<TopicKey>();
        streamKeyStrategy.GetRedisKey(topicKey).Returns(new RedisKey("app:st:topicA"));
        var options = new CacheOptions { Separator = ':', AppShortName = "app" };

        var sut = new StreamSuffixChannelStrategy(streamKeyStrategy, options, name);

        var channel = sut.GetRedisChannel(topicKey);

        channel.ToString().Should().Be("app:st:topicA:" + StreamSuffixChannelStrategy.DefaultName);
    }
}

public class StreamSuffixShardedChannelStrategyTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void GetRedisChannel_wraps_stream_key_as_hash_tag_when_stream_has_no_tag()
    {
        var streamKeyStrategy = _fixture.Freeze<IRedisStreamKeyStrategy>();
        var topicKey = _fixture.Create<TopicKey>();
        streamKeyStrategy.GetRedisKey(topicKey).Returns(new RedisKey("app:st:topicA"));
        var options = new CacheOptions { Separator = ':', AppShortName = "app" };

        var sut = new StreamSuffixShardedChannelStrategy(streamKeyStrategy, options, "notify");

        var channel = sut.GetRedisChannel(topicKey);

        channel.ToString().Should().Be("{app:st:topicA}:notify");
        channel.IsSharded.Should().BeTrue();
    }

    [Fact]
    public void GetRedisChannel_inherits_existing_hash_tag_from_stream_key()
    {
        var streamKeyStrategy = _fixture.Freeze<IRedisStreamKeyStrategy>();
        var topicKey = _fixture.Create<TopicKey>();
        streamKeyStrategy.GetRedisKey(topicKey).Returns(new RedisKey("app:st:{topicA}"));
        var options = new CacheOptions { Separator = ':', AppShortName = "app" };

        var sut = new StreamSuffixShardedChannelStrategy(streamKeyStrategy, options, "notify");

        var channel = sut.GetRedisChannel(topicKey);

        channel.ToString().Should().Be("app:st:{topicA}:notify");
        channel.IsSharded.Should().BeTrue();
    }

    [Theory]
    [InlineData("app:st:{}topicA")]
    [InlineData("app:st:{topicA")]
    [InlineData("app:st:topicA}")]
    public void GetRedisChannel_throws_when_stream_key_has_braces_but_no_valid_hash_tag(string streamKey)
    {
        var streamKeyStrategy = _fixture.Freeze<IRedisStreamKeyStrategy>();
        var topicKey = _fixture.Create<TopicKey>();
        streamKeyStrategy.GetRedisKey(topicKey).Returns(new RedisKey(streamKey));
        var options = new CacheOptions { Separator = ':', AppShortName = "app" };

        var sut = new StreamSuffixShardedChannelStrategy(streamKeyStrategy, options, "notify");

        var act = () => sut.GetRedisChannel(topicKey);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Sharded notify channel*slot affinity*");
    }

    [Fact]
    public void GetRedisChannel_falls_back_to_default_name()
    {
        var streamKeyStrategy = _fixture.Freeze<IRedisStreamKeyStrategy>();
        var topicKey = _fixture.Create<TopicKey>();
        streamKeyStrategy.GetRedisKey(topicKey).Returns(new RedisKey("app:st:{topicA}"));
        var options = new CacheOptions { Separator = ':', AppShortName = "app" };

        var sut = new StreamSuffixShardedChannelStrategy(streamKeyStrategy, options, name: null);

        var channel = sut.GetRedisChannel(topicKey);

        channel.ToString().Should().Be("app:st:{topicA}:" + StreamSuffixChannelStrategy.DefaultName);
        channel.IsSharded.Should().BeTrue();
    }
}

public class StreamSuffixChannelFactoryTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void Create_returns_literal_strategy_when_sharded_is_false()
    {
        var streamKeyStrategy = _fixture.Freeze<IRedisStreamKeyStrategy>();
        var options = new CacheOptions { Separator = ':', AppShortName = "app" };

        var sut = StreamSuffixChannel.Create(streamKeyStrategy, options, "notify", sharded: false);

        sut.Should().BeOfType<StreamSuffixChannelStrategy>();
    }

    [Fact]
    public void Create_returns_sharded_strategy_when_sharded_is_true()
    {
        var streamKeyStrategy = _fixture.Freeze<IRedisStreamKeyStrategy>();
        var options = new CacheOptions { Separator = ':', AppShortName = "app" };

        var sut = StreamSuffixChannel.Create(streamKeyStrategy, options, "notify", sharded: true);

        sut.Should().BeOfType<StreamSuffixShardedChannelStrategy>();
    }
}
