namespace UiPath.Caching.Tests.Broadcast;

public class GapFilteringTopicStateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_wrapper_reports_the_subscription_state_of_the_topic_it_wraps(bool subscribed)
    {
        var inner = Substitute.For<ITopic<ICacheEvent>, ITopicSubscriptionState>();
        inner.Should().BeAssignableTo<ITopicSubscriptionState>().Subject.IsSubscribed.Returns(subscribed);

        var sut = new GapFilteringTopic(inner);

        sut.IsSubscribed.Should().Be(subscribed);
    }

    [Fact]
    public void A_wrapped_topic_that_cannot_say_is_not_reported_subscribed()
    {
        var sut = new GapFilteringTopic(Substitute.For<ITopic<ICacheEvent>>());

        sut.IsSubscribed.Should().BeFalse();
    }
}
