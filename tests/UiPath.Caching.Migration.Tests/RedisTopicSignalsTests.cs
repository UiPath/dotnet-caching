using StackExchange.Redis;
using UiPath.Caching.Broadcast.Redis;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class RedisTopicSignalsTests
{
    [Fact]
    public async Task A_stream_is_ready_only_after_the_fallback_group_creation_finishes()
    {
        var signals = new RedisTopicSignals("items");
        var fallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var db = InterceptingProxy.Wrap<IDatabase>(null!, (method, _) => method.Name switch
        {
            nameof(IDatabase.ExecuteAsync) => Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.Unknown, CommandFlags.None, "unknown argument ENTRIESREAD")),
            nameof(IDatabase.StreamCreateConsumerGroupAsync) => fallback.Task,
            _ => throw new InvalidOperationException(method.Name),
        });
        var ready = signals.WaitAsync(CancellationToken.None);
        await signals.Observe(Doorbell()).SubscribeAsync(RedisChannel.Literal("test:items:notify"), (_, _) => { });
        var head = new StreamHead(0, 0, false, RedisValue.Null, null, 0, new ReadCount("0-0", 0));

        var creating = ConsumerGroups.CreateAsync(signals.Observe(db), "test:items", "node", StreamPosition.NewMessages, head);

        ready.IsCompleted.Should().BeFalse();
        fallback.SetResult(true);
        (await creating).Should().BeTrue();
        ready.IsCompletedSuccessfully.Should().BeTrue("a stream is ready once its fallback group creation has returned");
    }

    [Fact]
    public async Task A_stream_is_ready_only_after_its_doorbell_is_subscribed()
    {
        var signals = new RedisTopicSignals("items");
        var db = InterceptingProxy.Wrap<IDatabase>(null!, (method, _) =>
            method.Name == nameof(IDatabase.StreamCreateConsumerGroupAsync) ? Task.FromResult(true) : throw new InvalidOperationException(method.Name));
        var ready = signals.WaitAsync(CancellationToken.None);
        await signals.Observe(db).StreamCreateConsumerGroupAsync("test:items", "node", StreamPosition.NewMessages);

        ready.IsCompleted.Should().BeFalse("with no doorbell the reader waits out its poll, which a stopped clock never ends");
        await signals.Observe(Doorbell()).SubscribeAsync(RedisChannel.Literal("test:items:notify"), (_, _) => { });

        ready.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_a_pub_sub_topic_signals_readiness_from_a_subscription(bool streams)
    {
        var signals = new RedisTopicSignals("items", streams);
        var subscriber = InterceptingProxy.Wrap<ISubscriber>(null!, (method, _) =>
            method.Name == nameof(ISubscriber.Subscribe) ? null : throw new InvalidOperationException(method.Name));

        signals.Observe(subscriber).Subscribe(RedisChannel.Literal("test:items"), (_, _) => { });

        signals.WaitAsync(CancellationToken.None).IsCompletedSuccessfully.Should().Be(!streams);
    }

    private static ISubscriber Doorbell() => InterceptingProxy.Wrap<ISubscriber>(null!, (method, _) =>
        method.Name == nameof(ISubscriber.SubscribeAsync) ? Task.CompletedTask : throw new InvalidOperationException(method.Name));
}
