using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Broadcast;

public class RedisStreamsTopicOptionsValidatorTests
{
    [Fact]
    public void The_defaults_are_valid()
    {
        Validate(new RedisStreamsTopicOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-250)]
    public void A_notified_interval_that_is_not_positive_is_refused(int milliseconds)
    {
        var result = Validate(new RedisStreamsTopicOptions { NotifiedPollInterval = TimeSpan.FromMilliseconds(milliseconds) });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("greater than zero");
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(25, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void The_doorbell_interval_is_taken_between_zero_and_one_second(int milliseconds, bool valid)
    {
        var result = Validate(new RedisStreamsTopicOptions { NotifyMinInterval = TimeSpan.FromMilliseconds(milliseconds) });

        result.Succeeded.Should().Be(valid);
        if (!valid)
        {
            result.FailureMessage.Should().Contain("NotifyMinInterval");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_poll_interval_that_is_not_positive_is_refused(int milliseconds)
    {
        var result = Validate(new RedisStreamsTopicOptions { PollInterval = TimeSpan.FromMilliseconds(milliseconds) });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("PollInterval must be greater than zero");
    }

    [Fact]
    public void A_poll_interval_beyond_what_a_wait_takes_is_refused()
    {
        var result = Validate(new RedisStreamsTopicOptions { PollInterval = TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromMilliseconds(1) });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("PollInterval must be at most");
    }

    [Fact]
    public void A_notified_interval_beyond_what_a_wait_takes_is_refused()
    {
        var result = Validate(new RedisStreamsTopicOptions { NotifiedPollInterval = TimeSpan.MaxValue });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("NotifiedPollInterval must be at most");
    }

    [Fact]
    public void The_longest_interval_a_wait_takes_is_valid()
    {
        var longest = TimeSpan.FromMilliseconds(int.MaxValue);

        Validate(new RedisStreamsTopicOptions { PollInterval = longest, NotifiedPollInterval = longest }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_poll_interval_that_is_not_positive_is_refused_with_a_notified_interval_set()
    {
        var result = Validate(new RedisStreamsTopicOptions { PollInterval = TimeSpan.Zero, NotifiedPollInterval = TimeSpan.FromSeconds(5) });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("PollInterval must be greater than zero");
    }

    [Fact]
    public void A_notified_interval_below_the_poll_interval_is_refused()
    {
        var result = Validate(new RedisStreamsTopicOptions { PollInterval = TimeSpan.FromSeconds(1), NotifiedPollInterval = TimeSpan.FromMilliseconds(999) });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("greater than or equal to PollInterval");
    }

    [Fact]
    public void A_notified_interval_equal_to_the_poll_interval_is_valid()
    {
        Validate(new RedisStreamsTopicOptions { PollInterval = TimeSpan.FromSeconds(1), NotifiedPollInterval = TimeSpan.FromSeconds(1) }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Neither_set_waits_5_s_while_the_doorbell_is_live()
    {
        var options = new RedisStreamsTopicOptions();

        options.PollInterval.Should().Be(TimeSpan.FromMilliseconds(250));
        options.EffectiveNotifiedPollInterval.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(250)]
    [InlineData(12_000)]
    public void A_set_poll_interval_is_kept_while_the_doorbell_is_live(int milliseconds)
    {
        var options = new RedisStreamsTopicOptions { PollInterval = TimeSpan.FromMilliseconds(milliseconds) };

        options.EffectiveNotifiedPollInterval.Should().Be(options.PollInterval, "a set PollInterval, even 250 ms, keeps today's cadence");
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_set_notified_interval_applies_with_the_poll_interval_unset()
    {
        var options = new RedisStreamsTopicOptions { NotifiedPollInterval = TimeSpan.FromSeconds(2) };

        options.EffectiveNotifiedPollInterval.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_notified_interval_below_the_default_poll_interval_is_refused_with_the_poll_interval_unset()
    {
        var result = Validate(new RedisStreamsTopicOptions { NotifiedPollInterval = TimeSpan.FromMilliseconds(249) });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("greater than or equal to PollInterval");
    }

    [Fact]
    public void A_set_notified_interval_applies_with_the_poll_interval_set()
    {
        var options = new RedisStreamsTopicOptions { PollInterval = TimeSpan.FromSeconds(1), NotifiedPollInterval = TimeSpan.FromSeconds(3) };

        options.EffectiveNotifiedPollInterval.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void A_clone_of_the_defaults_keeps_the_5_s_default()
    {
        new RedisStreamsTopicOptions().Clone().EffectiveNotifiedPollInterval.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_clone_keeps_a_set_poll_interval_set()
    {
        var clone = new RedisStreamsTopicOptions { PollInterval = TimeSpan.FromMilliseconds(250) }.Clone();

        clone.PollInterval.Should().Be(TimeSpan.FromMilliseconds(250));
        clone.EffectiveNotifiedPollInterval.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Theory]
    [InlineData(null, 5_000)]
    [InlineData("00:00:00.250", 250)]
    [InlineData("00:00:01", 1_000)]
    public void The_section_sets_the_poll_interval_only_when_it_has_the_key(string? pollInterval, int liveMilliseconds)
    {
        var values = new Dictionary<string, string?> { ["Broadcast:RedisStreams:Enabled"] = "true" };
        if (pollInterval is not null)
        {
            values["Broadcast:RedisStreams:PollInterval"] = pollInterval;
        }
        var services = new ServiceCollection();
        new CachingBuilder(services, new ConfigurationBuilder().AddInMemoryCollection(values).Build()).AddRedisStreams();

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<RedisStreamsTopicOptions>>().Value;

        options.EffectiveNotifiedPollInterval.Should().Be(TimeSpan.FromMilliseconds(liveMilliseconds));
    }

    [Theory]
    [InlineData(null, null, 5_000)]
    [InlineData("00:00:01", null, 1_000)]
    [InlineData(null, "00:00:02", 2_000)]
    public void A_topic_override_starts_from_the_global_options(string? globalPoll, string? topicPoll, int liveMilliseconds)
    {
        var values = new Dictionary<string, string?> { ["Topics:0:Name"] = "orders", ["Topics:0:MaxLength"] = "10" };
        if (topicPoll is not null)
        {
            values["Topics:0:PollInterval"] = topicPoll;
        }
        var global = new RedisStreamsTopicOptions();
        if (globalPoll is not null)
        {
            global.PollInterval = TimeSpan.Parse(globalPoll, System.Globalization.CultureInfo.InvariantCulture);
        }
        var registry = new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("Topics"));

        var topic = registry.Resolve(new TopicKey("orders"), global.Clone)!;

        topic.EffectiveNotifiedPollInterval.Should().Be(TimeSpan.FromMilliseconds(liveMilliseconds), "a PollInterval set globally or for the topic counts as set");
    }

    [Fact]
    public void AddRedisStreams_refuses_an_invalid_notified_interval()
    {
        var builder = new CachingBuilder(new ServiceCollection(), new ConfigurationBuilder().Build());

        var act = () => builder.AddRedisStreams(o => { o.Enabled = true; o.NotifiedPollInterval = TimeSpan.FromMilliseconds(10); });

        act.Should().Throw<OptionsValidationException>().WithMessage("*NotifiedPollInterval*");
    }

    [Fact]
    public void AddRedisStreams_binds_the_notified_interval_from_the_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Broadcast:RedisStreams:NotifiedPollInterval"] = "00:00:03" })
            .Build();
        var services = new ServiceCollection();
        new CachingBuilder(services, configuration).AddRedisStreams();

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<RedisStreamsTopicOptions>>().Value;

        options.NotifiedPollInterval.Should().Be(TimeSpan.FromSeconds(3));
    }

    private static ValidateOptionsResult Validate(RedisStreamsTopicOptions options) =>
        new RedisStreamsTopicOptionsValidator().Validate(null, options);
}
