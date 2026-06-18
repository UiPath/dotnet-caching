using System.Threading.Channels;

namespace UiPath.Caching.Tests.Broadcast;

public class EventDispatcherTests
{
    [Fact]
    public async Task Consume_continues_processing_after_subject_throws_on_one_item()
    {
        var channel = Channel.CreateUnbounded<ICacheEvent>();
        var subject = new RecordingSubject(throwOnKey: "poison");

        using var dispatcher = new EventDispatcher<ICacheEvent>(
            "topic",
            channel.Reader,
            subject,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EventDispatcher<ICacheEvent>>.Instance,
            CancellationToken.None);

        channel.Writer.TryWrite(CreateEvent("ok-1"));
        channel.Writer.TryWrite(CreateEvent("poison"));
        channel.Writer.TryWrite(CreateEvent("ok-2"));
        channel.Writer.Complete();

        // The consume loop exits once the completed channel is fully drained — deterministic, unlike
        // polling Keys.Count in a short window (which is flaky under thread-pool starvation on CI).
        await dispatcher.ConsumeTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        subject.Keys.Should().ContainInOrder("ok-1", "poison", "ok-2");
        subject.ThrowCount.Should().Be(1);
    }

    [Fact]
    public async Task Consume_continues_processing_when_subject_throws_repeatedly()
    {
        var channel = Channel.CreateUnbounded<ICacheEvent>();
        var subject = new RecordingSubject(throwAlways: true);

        using var dispatcher = new EventDispatcher<ICacheEvent>(
            "topic",
            channel.Reader,
            subject,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EventDispatcher<ICacheEvent>>.Instance,
            CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            channel.Writer.TryWrite(CreateEvent($"e{i}"));
        }
        channel.Writer.Complete();

        await dispatcher.ConsumeTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        subject.ThrowCount.Should().Be(5, "every OnNext call must be attempted even when previous calls threw");
    }

    private static ICacheEvent CreateEvent(string key) => new TestCacheEvent
    {
        Id = Guid.NewGuid().ToString(),
        Source = new Uri("urn:test"),
        Data = new CacheEventData(key),
    };

    private sealed class RecordingSubject : IEventSubject<ICacheEvent>
    {
        private readonly string? _throwOnKey;
        private readonly bool _throwAlways;

        public RecordingSubject(string? throwOnKey = null, bool throwAlways = false)
        {
            _throwOnKey = throwOnKey;
            _throwAlways = throwAlways;
        }

        public List<string?> Keys { get; } = new();
        public int ThrowCount { get; private set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer) => Disposable.Empty;

        public void OnNext(ICacheEvent value)
        {
            lock (Keys) { Keys.Add(value.Key); }

            if (_throwAlways || value.Key == _throwOnKey)
            {
                ThrowCount++;
                throw new InvalidOperationException("subject boom");
            }
        }

        public void OnCompleted() { }
        public void Dispose() { }

        private sealed class Disposable : IDisposable
        {
            public static readonly Disposable Empty = new();
            public void Dispose() { }
        }
    }
}
