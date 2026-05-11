using System.Collections.Concurrent;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class KeyedSubjectTests
{
    private readonly KeyedSubject<ICacheEvent> _sut = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<KeyedSubject<ICacheEvent>>.Instance);

    [Fact]
    public void OnNext_dispatches_to_keyed_observer_matching_key()
    {
        var observer = new TestKeyedObserver("myKey");
        _sut.Subscribe(observer);

        var cacheEvent = CreateEvent("myKey");
        _sut.OnNext(cacheEvent);

        observer.Events.Should().ContainSingle().Which.Should().BeSameAs(cacheEvent);
    }

    [Fact]
    public void OnNext_does_not_dispatch_to_keyed_observer_with_different_key()
    {
        var observer = new TestKeyedObserver("keyA");
        _sut.Subscribe(observer);

        _sut.OnNext(CreateEvent("keyB"));

        observer.Events.Should().BeEmpty();
    }

    [Fact]
    public void OnNext_keyed_lookup_is_case_insensitive()
    {
        var observer = new TestKeyedObserver("MyKey");
        _sut.Subscribe(observer);

        var cacheEvent = CreateEvent("MYKEY");
        _sut.OnNext(cacheEvent);

        observer.Events.Should().ContainSingle();
    }

    [Fact]
    public void OnNext_dispatches_to_broadcast_observer_for_all_keys()
    {
        var broadcast = new TestBroadcastObserver();
        _sut.Subscribe(broadcast);

        var event1 = CreateEvent("key1");
        var event2 = CreateEvent("key2");
        _sut.OnNext(event1);
        _sut.OnNext(event2);

        broadcast.Events.Should().HaveCount(2);
    }

    [Fact]
    public void OnNext_dispatches_to_both_keyed_and_broadcast_observers()
    {
        var keyed = new TestKeyedObserver("key1");
        var broadcast = new TestBroadcastObserver();
        _sut.Subscribe(keyed);
        _sut.Subscribe(broadcast);

        var cacheEvent = CreateEvent("key1");
        _sut.OnNext(cacheEvent);

        keyed.Events.Should().ContainSingle();
        broadcast.Events.Should().ContainSingle();
    }

    [Fact]
    public void OnNext_with_null_key_dispatches_only_to_broadcast_observers()
    {
        var keyed = new TestKeyedObserver("key1");
        var broadcast = new TestBroadcastObserver();
        _sut.Subscribe(keyed);
        _sut.Subscribe(broadcast);

        var cacheEvent = CreateEvent(null);
        _sut.OnNext(cacheEvent);

        keyed.Events.Should().BeEmpty();
        broadcast.Events.Should().ContainSingle();
    }

    [Fact]
    public void OnNext_does_not_propagate_observer_exception()
    {
        _sut.Subscribe(new ThrowingKeyedObserver("key1"));

        Action act = () => _sut.OnNext(CreateEvent("key1"));

        act.Should().NotThrow();
    }

    [Fact]
    public void OnNext_continues_to_remaining_keyed_observers_when_one_throws()
    {
        var thrower = new ThrowingKeyedObserver("key1");
        var survivor = new TestKeyedObserver("key1");
        _sut.Subscribe(thrower);
        _sut.Subscribe(survivor);

        _sut.OnNext(CreateEvent("key1"));

        survivor.Events.Should().ContainSingle("sibling keyed observer must still receive the event when another throws");
    }

    [Fact]
    public void OnNext_continues_to_broadcast_observers_when_keyed_observer_throws()
    {
        var thrower = new ThrowingKeyedObserver("key1");
        var broadcast = new TestBroadcastObserver();
        _sut.Subscribe(thrower);
        _sut.Subscribe(broadcast);

        _sut.OnNext(CreateEvent("key1"));

        broadcast.Events.Should().ContainSingle("broadcast observer must still receive the event when a keyed observer throws");
    }

    [Fact]
    public void OnNext_continues_to_remaining_broadcast_observers_when_one_throws()
    {
        var thrower = new ThrowingBroadcastObserver();
        var survivor = new TestBroadcastObserver();
        _sut.Subscribe(thrower);
        _sut.Subscribe(survivor);

        _sut.OnNext(CreateEvent("key1"));

        survivor.Events.Should().ContainSingle("sibling broadcast observer must still receive the event when another throws");
    }

    [Fact]
    public void OnNext_logs_slow_observer_when_call_exceeds_threshold()
    {
        var logger = new RecordingLogger();
        var sut = new KeyedSubject<ICacheEvent>(logger, TimeSpan.FromMilliseconds(20));
        sut.Subscribe(new DelayingKeyedObserver("key1", TimeSpan.FromMilliseconds(80)));

        sut.OnNext(CreateEvent("key1"));

        logger.Records.Should().Contain(r => r.Contains("Slow observer") && r.Contains("OnNext"),
            "an observer slower than the threshold must trigger a slow-observer warning");
    }

    [Fact]
    public void OnNext_does_not_log_slow_observer_when_call_is_fast()
    {
        var logger = new RecordingLogger();
        var sut = new KeyedSubject<ICacheEvent>(logger, TimeSpan.FromMilliseconds(500));
        sut.Subscribe(new TestKeyedObserver("key1"));

        sut.OnNext(CreateEvent("key1"));

        logger.Records.Should().NotContain(r => r.Contains("Slow observer"),
            "a fast observer must not trigger the slow-observer warning");
    }

    [Fact]
    public void OnCompleted_completes_remaining_observers_when_one_throws()
    {
        var thrower = new ThrowingKeyedObserver("key1");
        var survivor = new TestKeyedObserver("key1");
        var broadcast = new TestBroadcastObserver();
        _sut.Subscribe(thrower);
        _sut.Subscribe(survivor);
        _sut.Subscribe(broadcast);

        Action act = () => _sut.OnCompleted();

        act.Should().NotThrow();
        survivor.Completed.Should().BeTrue();
        broadcast.Completed.Should().BeTrue();
    }

    [Fact]
    public void Unsubscribe_removes_keyed_observer()
    {
        var observer = new TestKeyedObserver("key1");
        var subscription = _sut.Subscribe(observer);

        subscription.Dispose();
        _sut.OnNext(CreateEvent("key1"));

        observer.Events.Should().BeEmpty();
    }

    [Fact]
    public void Unsubscribe_removes_broadcast_observer()
    {
        var observer = new TestBroadcastObserver();
        var subscription = _sut.Subscribe(observer);

        subscription.Dispose();
        _sut.OnNext(CreateEvent("key1"));

        observer.Events.Should().BeEmpty();
    }

    [Fact]
    public void OnCompleted_notifies_all_observers()
    {
        var keyed = new TestKeyedObserver("key1");
        var broadcast = new TestBroadcastObserver();
        _sut.Subscribe(keyed);
        _sut.Subscribe(broadcast);

        _sut.OnCompleted();

        keyed.Completed.Should().BeTrue();
        broadcast.Completed.Should().BeTrue();
    }

    [Fact]
    public void OnCompleted_prevents_further_dispatch()
    {
        var observer = new TestKeyedObserver("key1");
        _sut.Subscribe(observer);

        _sut.OnCompleted();
        _sut.OnNext(CreateEvent("key1"));

        observer.Events.Should().BeEmpty();
    }

    [Fact]
    public void Subscribe_after_completed_calls_OnCompleted_immediately()
    {
        _sut.OnCompleted();

        var observer = new TestKeyedObserver("key1");
        _sut.Subscribe(observer);

        observer.Completed.Should().BeTrue();
        observer.Events.Should().BeEmpty();
    }

    [Fact]
    public void Multiple_keyed_observers_with_same_key_all_receive_events()
    {
        var observer1 = new TestKeyedObserver("key1");
        var observer2 = new TestKeyedObserver("key1");
        _sut.Subscribe(observer1);
        _sut.Subscribe(observer2);

        _sut.OnNext(CreateEvent("key1"));

        observer1.Events.Should().ContainSingle();
        observer2.Events.Should().ContainSingle();
    }

    [Fact]
    public void Unsubscribe_one_keyed_observer_leaves_others_intact()
    {
        var observer1 = new TestKeyedObserver("key1");
        var observer2 = new TestKeyedObserver("key1");
        var sub1 = _sut.Subscribe(observer1);
        _sut.Subscribe(observer2);

        sub1.Dispose();
        _sut.OnNext(CreateEvent("key1"));

        observer1.Events.Should().BeEmpty();
        observer2.Events.Should().ContainSingle();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var observer = new TestKeyedObserver("key1");
        var subscription = _sut.Subscribe(observer);

        subscription.Dispose();
        subscription.Dispose(); // should not throw

        observer.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_subscribe_unsubscribe_is_thread_safe()
    {
        const int iterations = 1000;
        var barrier = new Barrier(3);

        var subscribeTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            var subs = new List<IDisposable>();
            for (var i = 0; i < iterations; i++)
            {
                subs.Add(_sut.Subscribe(new TestKeyedObserver($"key{i}")));
            }
            return subs;
        });

        var dispatchTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterations; i++)
            {
                _sut.OnNext(CreateEvent($"key{i}"));
            }
        });

        var unsubscribeTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterations; i++)
            {
                var sub = _sut.Subscribe(new TestKeyedObserver($"temp{i}"));
                sub.Dispose();
            }
        });

        await Task.WhenAll(subscribeTask, dispatchTask, unsubscribeTask);
    }

    [Fact]
    public void Dispose_calls_OnCompleted()
    {
        var observer = new TestKeyedObserver("key1");
        _sut.Subscribe(observer);

        _sut.Dispose();

        observer.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_subscribe_unsubscribe_on_same_key_does_not_lose_observers()
    {
        const int threadCount = 50;
        const int iterationsPerThread = 200;
        const string key = "contended-key";
        var barrier = new Barrier(threadCount);

        var survivors = new ConcurrentBag<(TestKeyedObserver observer, IDisposable subscription)>();

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterationsPerThread; i++)
            {
                var obs = new TestKeyedObserver(key);
                var sub = _sut.Subscribe(obs);
                sub.Dispose();
            }
            var final_obs = new TestKeyedObserver(key);
            var final_sub = _sut.Subscribe(final_obs);
            survivors.Add((final_obs, final_sub));
        })).ToArray();

        await Task.WhenAll(tasks);

        var evt = CreateEvent(key);
        _sut.OnNext(evt);

        foreach (var survivor in survivors)
        {
            survivor.observer.Events.Should().ContainSingle("every surviving observer must receive the event");
        }
    }

    [Fact]
    public async Task Unsubscribe_does_not_remove_other_observers_on_same_key()
    {
        const string key = "sentinel-key";
        const int churnIterations = 5000;

        var sentinel = new TestKeyedObserver(key);
        _sut.Subscribe(sentinel);

        var eventCount = 0;

        var churnTask = Task.Run(() =>
        {
            for (var i = 0; i < churnIterations; i++)
            {
                var obs = new TestKeyedObserver(key);
                var sub = _sut.Subscribe(obs);
                sub.Dispose();
            }
        });

        var dispatchTask = Task.Run(() =>
        {
            for (var i = 0; i < churnIterations; i++)
            {
                _sut.OnNext(CreateEvent(key));
                Interlocked.Increment(ref eventCount);
            }
        });

        await Task.WhenAll(churnTask, dispatchTask);

        sentinel.Events.Should().HaveCount(eventCount,
            "sentinel observer must receive every event; " +
            "unsubscribe of other observers must not remove the shared inner dictionary");
    }

    [Fact]
    public async Task Concurrent_subscribe_and_dispatch_delivers_to_all_subscribed_observers()
    {
        const string key = "dispatch-key";
        const int preSubscribed = 100;
        const int eventCount = 500;

        var preObservers = Enumerable.Range(0, preSubscribed)
            .Select(_ =>
            {
                var obs = new TestKeyedObserver(key);
                _sut.Subscribe(obs);
                return obs;
            })
            .ToList();

        var barrier = new Barrier(2);

        var lateObservers = new ConcurrentBag<TestKeyedObserver>();
        var lateTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < 50; i++)
            {
                var obs = new TestKeyedObserver(key);
                _sut.Subscribe(obs);
                lateObservers.Add(obs);
            }
        });

        var dispatchTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < eventCount; i++)
            {
                _sut.OnNext(CreateEvent(key));
            }
        });

        await Task.WhenAll(lateTask, dispatchTask);

        foreach (var obs in preObservers)
        {
            obs.Events.Should().HaveCount(eventCount,
                "pre-subscribed observers must receive every dispatched event");
        }
    }

    [Fact]
    public async Task High_volume_subscribe_unsubscribe_completes_without_error()
    {
        const int threadCount = 20;
        const int opsPerThread = 10_000;
        const int keyCount = 10;
        var barrier = new Barrier(threadCount);

        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < opsPerThread; i++)
            {
                var key = $"key{(t + i) % keyCount}";
                var obs = new NoOpKeyedObserver(key);
                var sub = _sut.Subscribe(obs);
                _sut.OnNext(CreateEvent(key));
                sub.Dispose();
            }
        })).ToArray();

        await Task.WhenAll(tasks);
    }

    private static ICacheEvent CreateEvent(string? key)
    {
        return new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:test"),
            Data = key != null ? new CacheEventData(key) : null
        };
    }

    private sealed class TestKeyedObserver(string key) : IKeyedObserver<ICacheEvent>
    {
        public string Key { get; } = key;
        public List<ICacheEvent> Events { get; } = [];
        public bool Completed { get; private set; }

        public void OnNext(ICacheEvent value) => Events.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() => Completed = true;
    }

    private sealed class TestBroadcastObserver : IObserver<ICacheEvent>
    {
        public List<ICacheEvent> Events { get; } = [];
        public bool Completed { get; private set; }

        public void OnNext(ICacheEvent value) => Events.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() => Completed = true;
    }

    private sealed class NoOpKeyedObserver(string key) : IKeyedObserver<ICacheEvent>
    {
        public string Key { get; } = key;
        public void OnNext(ICacheEvent value) { }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class ThrowingKeyedObserver(string key) : IKeyedObserver<ICacheEvent>
    {
        public string Key { get; } = key;
        public void OnNext(ICacheEvent value) => throw new InvalidOperationException("boom on next");
        public void OnError(Exception error) { }
        public void OnCompleted() => throw new InvalidOperationException("boom on completed");
    }

    private sealed class ThrowingBroadcastObserver : IObserver<ICacheEvent>
    {
        public void OnNext(ICacheEvent value) => throw new InvalidOperationException("boom on next");
        public void OnError(Exception error) { }
        public void OnCompleted() => throw new InvalidOperationException("boom on completed");
    }

    private sealed class DelayingKeyedObserver(string key, TimeSpan delay) : IKeyedObserver<ICacheEvent>
    {
        public string Key { get; } = key;
        public void OnNext(ICacheEvent value) => Thread.Sleep(delay);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Records { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Records.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
