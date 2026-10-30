using System.Collections.Concurrent;

namespace UiPath.Caching;

/// <summary>Moves the stripe of every key a peer changes, so a fill from an inner read begun before the change is refused, held key or not.</summary>
internal sealed partial class PeerWriteGuard(BackgroundWrites background, ITopicProvider topics, Uri? source, TimeProvider clock, ILogger logger) : IDisposable
{
    /// <summary>How long a topic that could not be watched waits before a read tries again.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = logger;
    // Compared as the change tokens compare it, without a Uri.Compare allocation per event.
    private readonly string? _source = Text(source);
    private readonly ConcurrentDictionary<TopicKey, Watcher> _watched = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Hears the topic from now on, before a read's mark: a peer's write the read may miss is heard after it.</summary>
    public void Watch(TopicKey topic)
    {
        if (_watched.TryGetValue(topic, out var watcher) && watcher.Covers(clock))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || (_watched.TryGetValue(topic, out watcher) && watcher.Covers(clock)))
            {
                return;
            }

            background.Track();
            var failedBefore = watcher?.Failed == true;
            var next = new Watcher(this, topic);
            // Before the subscription: a topic that completes while it subscribes removes this one.
            _watched[topic] = next;
            try
            {
                next.Attach(topics.Create(topic).Subscribe(next));
            }
            catch (Exception ex)
            {
                next.Fail(clock.GetTimestamp());
                if (failedBefore)
                {
                    LogWatchStillFailing(ex, topic);
                }
                else
                {
                    LogWatchFailed(ex, topic);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        foreach (var watcher in _watched.Values)
        {
            watcher.End();
        }

        _watched.Clear();
    }

    private static string? Text(Uri? uri) => uri switch
    {
        null => null,
        { IsAbsoluteUri: true } => uri.AbsoluteUri,
        _ => uri.OriginalString,
    };

    private static bool Is(string? type, string known) => string.Equals(type, known, StringComparison.OrdinalIgnoreCase);

    // A refresh that only renews the expiry leaves what a read found as it was; one that carries metadata changes it.
    private static bool ChangesMetadata(ICacheEvent value) =>
        Is(value.Type, KnownEventTypes.CacheRefreshed) && value.Data?.Properties?.ContainsKey(KnownFieldNames.MetadataKey) == true;

    private void Heard(ICacheEvent value)
    {
        if (string.Equals(_source, Text(value.Source), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var type = value.Type;
        if (Is(type, KnownEventTypes.TopicInvalidated))
        {
            background.TouchAll();
        }
        else if (value.Key is { } key && (Is(type, KnownEventTypes.CacheSet) || Is(type, KnownEventTypes.CacheRemoved) || ChangesMetadata(value)))
        {
            background.Touch(key);
        }
    }

    private void Lost() => background.TouchAll();

    /// <summary>The topic ended: events published on it again are heard only once a read watches it again.</summary>
    private void Ended(Watcher watcher)
    {
        _watched.TryRemove(new KeyValuePair<TopicKey, Watcher>(watcher.Topic, watcher));
        watcher.End();
        background.TouchAll();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not watch topic {Topic} for peers' writes; a read racing one may keep the older value until it expires locally. Tried again on a read after the retry delay.")]
    private partial void LogWatchFailed(Exception ex, TopicKey topic);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not watch topic {Topic} for peers' writes again.")]
    private partial void LogWatchStillFailing(Exception ex, TopicKey topic);

    /// <summary>The guard's subscription to one topic.</summary>
    private sealed class Watcher(PeerWriteGuard guard, TopicKey topic) : IFirstObserver<ICacheEvent>, IMissedEventsObserver
    {
        private IDisposable? _subscription;
        private int _ended;
        private long _failedAt;
        private volatile bool _failed;

        public TopicKey Topic => topic;

        public bool Failed => _failed;

        /// <summary>Subscribed, or failed too recently to try again.</summary>
        public bool Covers(TimeProvider clock) => !_failed || clock.GetElapsedTime(Volatile.Read(ref _failedAt)) < RetryDelay;

        public void Fail(long at)
        {
            Volatile.Write(ref _failedAt, at);
            _failed = true;
        }

        public void Attach(IDisposable subscription)
        {
            Volatile.Write(ref _subscription, subscription);
            if (Volatile.Read(ref _ended) != 0)
            {
                Interlocked.Exchange(ref _subscription, null)?.Dispose();
            }
        }

        public void End()
        {
            Interlocked.Exchange(ref _ended, 1);
            Interlocked.Exchange(ref _subscription, null)?.Dispose();
        }

        public void OnNext(ICacheEvent value) => guard.Heard(value);

        public void OnEventsMissed(MissedEventsReason reason) => guard.Lost();

        public void OnError(Exception error) => guard.Ended(this);

        public void OnCompleted() => guard.Ended(this);
    }
}
