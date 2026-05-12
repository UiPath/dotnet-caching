using UiPath.Platform.Caching.Broadcast;
using UiPath.Platform.Caching.Broadcast.Redis;
using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class PerTopicOptionsRegistryTests
{
    [Fact]
    public void Constructor_throws_ArgumentNullException_on_null_topics_section()
    {
        var act = () => new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Configure_stores_action_and_GetActions_returns_it_case_insensitively()
    {
        var registry = new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(NullConfigurationSection.Instance);
        Action<RedisStreamsTopicOptions> action = o => o.MaxLength = 999;

        registry.Configure("Orders", action);

        registry.GetActions("orders").Should().ContainSingle().Which.Should().BeSameAs(action);
        registry.GetActions("ORDERS").Should().ContainSingle().Which.Should().BeSameAs(action);
    }

    [Fact]
    public void Configure_stacks_multiple_actions_for_the_same_topic_in_registration_order()
    {
        var registry = new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(NullConfigurationSection.Instance);
        Action<RedisStreamsTopicOptions> first = _ => { };
        Action<RedisStreamsTopicOptions> second = _ => { };

        registry.Configure("orders", first);
        registry.Configure("ORDERS", second);

        registry.GetActions("orders").Should().Equal(first, second);
    }

    [Fact]
    public void GetActions_invocation_applies_actions_in_order_with_last_writer_wins_per_property()
    {
        var registry = new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(NullConfigurationSection.Instance);
        registry.Configure("orders", o => { o.MaxLength = 100; o.PollBatchSize = 10; });
        registry.Configure("orders", o => o.MaxLength = 200);

        var target = new RedisStreamsTopicOptions();
        foreach (var action in registry.GetActions("orders"))
        {
            action(target);
        }

        target.MaxLength.Should().Be(200, "second action overrides MaxLength");
        target.PollBatchSize.Should().Be(10, "PollBatchSize only set by first action stands");
    }

    [Fact]
    public void Configure_throws_ArgumentNullException_on_null_topic_name()
    {
        var registry = new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(NullConfigurationSection.Instance);

        var act = () => registry.Configure(null!, _ => { });

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Configure_throws_ArgumentException_on_blank_topic_name(string topicName)
    {
        var registry = new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(NullConfigurationSection.Instance);

        var act = () => registry.Configure(topicName, _ => { });

        act.Should().Throw<ArgumentException>().WithMessage("*non-empty*non-whitespace*");
    }

    [Fact]
    public void Configure_throws_ArgumentNullException_on_null_action()
    {
        var registry = new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(NullConfigurationSection.Instance);

        var act = () => registry.Configure("orders", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetActions_returns_empty_for_unknown_topic()
    {
        var registry = new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(NullConfigurationSection.Instance);

        registry.GetActions("nothing-registered").Should().BeEmpty();
    }

    [Fact]
    public void Resolve_throws_ArgumentNullException_on_null_clone()
    {
        var registry = new PerTopicOptionsRegistry<RedisStreamsTopicOptions>(NullConfigurationSection.Instance);

        var act = () => registry.Resolve(new TopicKey("orders"), clone: null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("clone");
    }
}
