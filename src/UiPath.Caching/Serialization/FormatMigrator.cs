using System.Collections.Concurrent;
using System.Threading.Channels;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Serialization;

/// <summary>Runs the rewrites a read offers off the reader's path, one at a time, within a rate, a queue length and a byte budget; a read never waits for one.</summary>
internal sealed partial class FormatMigrator : IDisposable
{
    internal const int DefaultCapacity = 1024;
    internal const int DefaultRatePerSecond = 100;
    internal const long DefaultMaxQueuedBytes = 32L * 1024 * 1024;
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    // A value that would not come out the same is not worth another look for a while; the set is bounded.
    internal static readonly TimeSpan LossyWindow = TimeSpan.FromHours(1);
    internal static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(30);

    private const int MaxLossyKeys = 50_000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly ICachingTelemetryProvider _telemetry;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly int _capacity;
    private readonly int _rate;
    private readonly long _maxBytes;
    private readonly long _windowTicks;
    private readonly Channel<Item> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<OfferKey, Seen> _seen = new();
    private readonly ConcurrentDictionary<(string Cache, RewriteOutcome Outcome), long[]> _refused = new();
    private long _sweptAt;
    private double _tokens;
    private long _refilledAt;
    private long _refusedUntil;
    private bool _queueFull;
    private long _gated;
    private long _bytes;
    private int _jobs;
    private int _lossyKeys;
    private Task? _worker;
    private ITimer? _flushTimer;
    private int _flushScheduled;
    private long _lastLogged;
    private int _suppressed;
    private bool _disposed;

    public FormatMigrator(
        ICachingTelemetryProvider telemetry,
        TimeProvider clock,
        ILogger logger,
        int capacity = DefaultCapacity,
        int ratePerSecond = DefaultRatePerSecond,
        TimeSpan? window = null,
        long maxQueuedBytes = DefaultMaxQueuedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ratePerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxQueuedBytes, 1L);
        _telemetry = telemetry;
        _clock = clock;
        _logger = logger;
        _capacity = capacity;
        _rate = ratePerSecond;
        _maxBytes = maxQueuedBytes;
        _windowTicks = Ticks(window ?? DefaultWindow);
        _tokens = ratePerSecond;
        _sweptAt = _refilledAt = clock.GetTimestamp();
        _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Why a value was not claimed: offered lately, or refused for the rate, the queue, the byte budget or shutdown, which holds for the other values of the same read too.</summary>
    internal enum ClaimResult
    {
        Claimed,
        Recent,
        Refused,
    }

    /// <summary>Completes once the queue is closed and the rewrite in flight, if any, has finished.</summary>
    public Task Stopped
    {
        get
        {
            lock (_gate)
            {
                return _worker ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>How many claims took the gate; for a test that pins the lock-free refusal.</summary>
    internal long GatedClaims => Volatile.Read(ref _gated);

    /// <summary>How many keys are held back as lossy; for a test of the bookkeeping.</summary>
    internal int LossyKeys => Volatile.Read(ref _lossyKeys);

    /// <summary>
    /// Whether <paramref name="start"/> was called, and its job queued. It is called only for a key not offered within the window, while the rate, the queue
    /// and the byte budget allow, so a refused offer allocates nothing and makes no copy.
    /// </summary>
    public bool TryEnqueue<TState>(object owner, RedisKey key, string cache, int bytes, TState state, Func<TState, Func<CancellationToken, ValueTask<RewriteTally>>> start)
    {
        if (TryClaim(owner, key, cache, bytes) != ClaimResult.Claimed)
        {
            return false;
        }

        return EnqueueClaimed(cache, [new OfferKey(owner, key)], bytes, state, start);
    }

    /// <summary>Admits one value, a string or one field of a hash: not offered lately, within the rate, the queue and the byte budget. A claim is then given to <see cref="EnqueueClaimed"/>.</summary>
    public ClaimResult TryClaim(object owner, RedisKey key, string cache, int bytes)
    {
        var offer = new OfferKey(owner, key);
        var now = _clock.GetTimestamp();
        if (Recent(offer, now))
        {
            return ClaimResult.Recent;
        }

        // Without the gate: while the rate or the queue is known to refuse, a read takes no lock.
        if (Volatile.Read(ref _queueFull) || now < Volatile.Read(ref _refusedUntil))
        {
            Refuse(cache, Volatile.Read(ref _queueFull) ? RewriteOutcome.Dropped : RewriteOutcome.RateLimited);
            return ClaimResult.Refused;
        }

        RewriteOutcome? refused = null;
        lock (_gate)
        {
            _gated++;
            if (_disposed)
            {
                return ClaimResult.Refused;
            }

            if (Recent(offer, now))
            {
                return ClaimResult.Recent;
            }

            Sweep(now);
            _tokens = Math.Min(_rate, _tokens + (_clock.GetElapsedTime(_refilledAt, now).TotalSeconds * _rate));
            _refilledAt = now;
            if (_jobs >= _capacity)
            {
                refused = RewriteOutcome.Dropped;
                Volatile.Write(ref _queueFull, true);
            }
            else if (_bytes + bytes > _maxBytes)
            {
                refused = RewriteOutcome.Dropped;
            }
            else if (_tokens < 1)
            {
                refused = RewriteOutcome.RateLimited;
                Volatile.Write(ref _refusedUntil, now + Ticks(TimeSpan.FromSeconds((1 - _tokens) / _rate)));
            }
            else
            {
                _tokens--;
                _bytes += bytes;
                if (_seen.TryGetValue(offer, out var expired) && expired.Lossy)
                {
                    _lossyKeys--;
                }

                _seen[offer] = new(now + _windowTicks, Lossy: false);
            }
        }

        if (refused is { } outcome)
        {
            Refuse(cache, outcome);
            return ClaimResult.Refused;
        }

        return ClaimResult.Claimed;
    }

    /// <summary>Queues the job for values that were claimed; if it cannot be made or queued, the claims are given back.</summary>
    public bool EnqueueClaimed<TState>(string cache, OfferKey[] claimed, int bytes, TState state, Func<TState, Func<CancellationToken, ValueTask<RewriteTally>>> start)
    {
        Func<CancellationToken, ValueTask<RewriteTally>> job;
        try
        {
            job = start(state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Release(claimed, bytes, refund: true);
            LogFailure(ex);
            Count(cache, RewriteOutcome.Failed, claimed.Length);
            return false;
        }

        lock (_gate)
        {
            if (!_disposed && _queue.Writer.TryWrite(new(cache, job, claimed, bytes)))
            {
                _jobs++;
                _worker ??= Task.Run(RunAsync);
                return true;
            }
        }

        Release(claimed, bytes, refund: true);
        Count(cache, RewriteOutcome.Dropped, claimed.Length);
        return false;
    }

    public void Dispose()
    {
        bool started;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            started = _worker is not null;
        }

        // Before the queue closes: the worker ends only once it has, and then disposes the source. It drops what it reads after that.
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker ended on its own.
        }

        _queue.Writer.TryComplete();
        if (!started)
        {
            _stop.Dispose();
        }

        _flushTimer?.Dispose();
        Flush();
    }

    /// <summary>Logs a failure that is not a rewrite's own, with the same once-an-interval line.</summary>
    internal void NoteFailure(Exception ex) => LogFailure(ex);

    /// <summary>Completes when nothing is queued or running; for a test that has to know a read's offer has been dealt with.</summary>
    internal async Task WhenIdleAsync(CancellationToken token)
    {
        while (Volatile.Read(ref _jobs) > 0)
        {
            await Task.Delay(1, token).ConfigureAwait(false);
        }
    }

    /// <summary>Reports the offers refused since the last report, a count for each outcome and cache; also on a timer, once a second at most, so a busy node does not send a metric per read.</summary>
    internal void Flush()
    {
        Volatile.Write(ref _flushScheduled, 0);
        foreach (var ((cache, outcome), counter) in _refused)
        {
            Count(cache, outcome, (int)Math.Min(int.MaxValue, Interlocked.Exchange(ref counter[0], 0)));
        }
    }

    private static string Tag(RewriteOutcome outcome) => outcome switch
    {
        RewriteOutcome.Done => "done",
        RewriteOutcome.Changed => "changed",
        RewriteOutcome.Lossy => "lossy",
        RewriteOutcome.Held => "held",
        RewriteOutcome.RateLimited => "ratelimited",
        RewriteOutcome.Dropped => "dropped",
        _ => "failed",
    };

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _clock.TimestampFrequency);

    private bool Recent(OfferKey offer, long now) => _seen.TryGetValue(offer, out var seen) && now < seen.Until;

    // Under the gate: what has aged out is forgotten once a window, so the map holds about what one window admitted, plus the lossy ones.
    private void Sweep(long now)
    {
        if (now - _sweptAt < _windowTicks)
        {
            return;
        }

        _sweptAt = now;
        foreach (var (key, seen) in _seen)
        {
            if (now >= seen.Until && _seen.TryRemove(key, out _) && seen.Lossy)
            {
                _lossyKeys--;
            }
        }
    }

    private void Release(OfferKey[] claimed, int bytes, bool refund)
    {
        lock (_gate)
        {
            _bytes -= bytes;
            if (refund)
            {
                _tokens = Math.Min(_rate, _tokens + claimed.Length);
                Volatile.Write(ref _refusedUntil, 0);
            }

            foreach (var key in claimed)
            {
                _seen.TryRemove(key, out _);
            }
        }
    }

    private void Finished(Item item, RewriteTally? tally)
    {
        lock (_gate)
        {
            _jobs--;
            _bytes -= item.Bytes;
            Volatile.Write(ref _queueFull, false);
            if (tally is { Lossy: > 0 } lossy)
            {
                var until = _clock.GetTimestamp() + Ticks(LossyWindow);
                foreach (var index in lossy.LossyAt ?? [.. Enumerable.Range(0, item.Keys.Length)])
                {
                    if (index < item.Keys.Length && _lossyKeys < MaxLossyKeys)
                    {
                        _lossyKeys++;
                        _seen[item.Keys[index]] = new(until, Lossy: true);
                    }
                }
            }
        }
    }

    private void Refuse(string cache, RewriteOutcome outcome)
    {
        Interlocked.Increment(ref _refused.GetOrAdd((cache, outcome), static _ => new long[1])[0]);
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_disposed)
            {
                _flushTimer?.Dispose();
                _flushTimer = _clock.CreateTimer(static state => ((FormatMigrator)state!).Flush(), this, FlushInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                RewriteTally? tally = null;
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        Count(item.Cache, RewriteOutcome.Dropped, item.Keys.Length);
                        continue;
                    }

                    tally = await RunOneAsync(item, token).ConfigureAwait(false);
                }
                finally
                {
                    Finished(item, tally);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailure(ex);
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task<RewriteTally?> RunOneAsync(Item item, CancellationToken token)
    {
        try
        {
            var tally = await item.Job(token).ConfigureAwait(false);
            Count(item.Cache, RewriteOutcome.Done, tally.Done);
            Count(item.Cache, RewriteOutcome.Changed, tally.Changed);
            Count(item.Cache, RewriteOutcome.Lossy, tally.Lossy);
            Count(item.Cache, RewriteOutcome.Held, tally.Held);
            return tally;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Count(item.Cache, RewriteOutcome.Dropped, item.Keys.Length);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailure(ex);
            Count(item.Cache, RewriteOutcome.Failed, item.Keys.Length);
        }

        return null;
    }

    // The first failure, then one line an interval saying how many came after it; and at shutdown, when a rewrite in flight fails because the connection closed, no warning.
    private void LogFailure(Exception ex)
    {
        if (Volatile.Read(ref _disposed))
        {
            LogRewriteFailedAtShutdown(ex);
            return;
        }

        var now = _clock.GetTimestamp();
        var last = Volatile.Read(ref _lastLogged);
        if (last == 0 || _clock.GetElapsedTime(last, now) >= LogInterval)
        {
            if (Interlocked.CompareExchange(ref _lastLogged, now == 0 ? 1 : now, last) == last)
            {
                LogRewriteFailed(ex, Interlocked.Exchange(ref _suppressed, 0));
                return;
            }
        }

        Interlocked.Increment(ref _suppressed);
    }

    private void Count(string cache, RewriteOutcome outcome, int values = 1)
    {
        if (values <= 0)
        {
            return;
        }

        try
        {
            _telemetry.TrackMetric(Metrics.SerializerRewrite, values, [new("outcome", Tag(outcome)), new("cache", cache)]);
        }
        catch (Exception ex)
        {
            _telemetry.TryTrackException(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rewriting a stored value in the policy's write format failed; the value is left as it is. {Suppressed} more failures since the last of these lines.")]
    private partial void LogRewriteFailed(Exception ex, int suppressed);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rewriting a stored value in the policy's write format failed while the node was shutting down.")]
    private partial void LogRewriteFailedAtShutdown(Exception ex);

    /// <summary>What an offer is about: a key of one cache, so another cache or provider with the same key name does not suppress it.</summary>
    internal readonly record struct OfferKey(object Owner, RedisKey Key)
    {
        public bool Equals(OfferKey other) => ReferenceEquals(Owner, other.Owner) && Key.Equals(other.Key);

        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Owner), Key);
    }

    private readonly record struct Seen(long Until, bool Lossy);

    private readonly record struct Item(string Cache, Func<CancellationToken, ValueTask<RewriteTally>> Job, OfferKey[] Keys, int Bytes);
}
