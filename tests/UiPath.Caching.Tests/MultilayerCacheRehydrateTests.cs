#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using System.Reflection;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests;

public class MultilayerCacheRehydrateTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{

    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);
    private static readonly string[] ErrorReason = ["error"];
    private readonly ManualTimeProvider _clock = new();
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ICache _innerCache = default!;
    private global::Microsoft.Extensions.Caching.Memory.IMemoryCache _memoryCache = default!;
    private ICacheKeyStrategy _cacheKeyStrategy = default!;
    private ITopicKeyStrategy _topicKeyStrategy = default!;
    private ITopicFactory _topicFactory = default!;
    private MultilayerCachePerNameLockTests.ITopicProviderWithConnectionState _topicProvider = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private global::UiPath.Caching.IMemoryCacheFactory _memoryCacheFactory = default!;
    private IDistributedLock _distributedLock = default!;
    private InMemoryRedisCacheOptions _options = default!;
    private CacheKey _cacheKey = default!;
    private TopicKey _topicKey = default!;
    private RecordingTelemetryProvider _telemetry = default!;
    private MultilayerCache? _sut;

    private MultilayerCache Sut => _sut ??= RunTables.Track(_fixture.Create<MultilayerCache>());

    [Fact]
    public async Task Hit_before_threshold_does_not_trigger_rehydrate()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var fresh = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().Add(Duration) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(fresh);

        var generatorCalls = 0;
        Task<string?> Generator(CancellationToken _)
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
        }

        var result = await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<string?>>)Generator, RehydratePolicy(), token);

        result.Should().Be("cached");
        generatorCalls.Should().Be(0);
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Hit_past_threshold_acquires_distributed_lock_and_invokes_generator()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> Generator(CancellationToken _) => generatorTcs.Task;

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        acquiredLock.DisposeAsync().Returns(_ => { _telemetry.Signal.Pulse(); return ValueTask.CompletedTask; });
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);

        var result = await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<string?>>)Generator, RehydratePolicy(), token);

        result.Should().Be("cached");

        await WaitForAsync(() => _distributedLock.ReceivedCalls().Any(), token);
        await _distributedLock.ReceivedWithAnyArgs(1).TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
        generatorTcs.TrySetResult("rehydrated");
    }

    /// <summary>The threshold measures against the configured lifetime, not a jitter draw; a year-wide draw would put this hit nowhere near it.</summary>
    [Fact]
    public async Task Hit_past_threshold_rehydrates_regardless_of_write_jitter()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var policy = new CachePolicy
        {
            DistributedExpiration = Duration,
            JitterMaxDuration = TimeSpan.FromDays(365),
            RehydrateEnabled = true,
            Rehydrate = new RehydrateOptions
            {
                Threshold = 0.75,
                BaseCooldown = TimeSpan.FromSeconds(1),
                MaxCooldown = TimeSpan.FromMinutes(5),
                TimeoutFraction = 0.5,
                Name = "test-profile",
            },
        };
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().AddMinutes(1) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Task<string?> Generator(CancellationToken _)
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
        }
        var acquiredLock = Substitute.For<IAsyncDisposable>();
        acquiredLock.DisposeAsync().Returns(_ => { _telemetry.Signal.Pulse(); return ValueTask.CompletedTask; });
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await Sut.GetOrAddAsync(_cacheKey, Generator, policy, token);

        result.Should().Be("cached");
        await WaitForAsync(() => Volatile.Read(ref generatorCalls) > 0, token);
    }

    [Fact]
    public async Task Rehydrate_write_keeps_a_caller_supplied_lifetime_exact()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var policy = new CachePolicy
        {
            JitterMaxDuration = TimeSpan.FromDays(365),
            RehydrateEnabled = true,
            Rehydrate = new RehydrateOptions
            {
                Threshold = 0.75,
                BaseCooldown = TimeSpan.FromSeconds(1),
                MaxCooldown = TimeSpan.FromMinutes(5),
                TimeoutFraction = 0.5,
                Name = "test-profile",
            },
        };
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().AddMinutes(1) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);
        static Task<string?> Generator(CancellationToken _) => Task.FromResult<string?>("rehydrated");
        var acquiredLock = Substitute.For<IAsyncDisposable>();
        acquiredLock.DisposeAsync().Returns(_ => { _telemetry.Signal.Pulse(); return ValueTask.CompletedTask; });
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        DateTimeOffset? written = null;
        _innerCache.SetAsync<string?>(_cacheKey, "rehydrated", Arg.Do<DateTimeOffset>(d => written = d), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, Generator, Duration, policy, token);

        await WaitForAsync(() => written is not null, token);
        written.Should().BeCloseTo(_clock.GetUtcNow().Add(Duration), TimeSpan.FromSeconds(5));
    }

    /// <summary>The write and the rehydrate threshold used to be separate chains, so an entry written under the library default was never rehydrated.</summary>
    [Fact]
    public async Task Hit_past_threshold_rehydrates_when_only_the_library_default_supplies_the_duration()
    {
        var token = testContextAccessor.Current.CancellationToken;
        _options.DefaultExpiration = null;
        var policy = new CachePolicy
        {
            RehydrateEnabled = true,
            Rehydrate = new RehydrateOptions
            {
                Threshold = 0.75,
                BaseCooldown = TimeSpan.FromSeconds(1),
                MaxCooldown = TimeSpan.FromMinutes(5),
                TimeoutFraction = 0.5,
                Name = "test-profile",
            },
        };
        // Five minutes left of the one-hour default is inside the last quarter, so past the threshold.
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().AddMinutes(5) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Task<string?> Generator(CancellationToken _)
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
        }
        var acquiredLock = Substitute.For<IAsyncDisposable>();
        acquiredLock.DisposeAsync().Returns(_ => { _telemetry.Signal.Pulse(); return ValueTask.CompletedTask; });
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await Sut.GetOrAddAsync(_cacheKey, Generator, policy, token);

        result.Should().Be("cached");
        await WaitForAsync(() => Volatile.Read(ref generatorCalls) > 0, token);
    }

    [Fact]
    public async Task Rehydrate_writes_value_back_through_inner_cache_on_success()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Task<string?> Generator(CancellationToken _) => Task.FromResult<string?>("rehydrated");

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        acquiredLock.DisposeAsync().Returns(_ => { _telemetry.Signal.Pulse(); return ValueTask.CompletedTask; });
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<string?>>)Generator, RehydratePolicy(), token);

        await WaitForAsync(() => _innerCache.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ICache.SetAsync)), token);
        await _innerCache.Received(1).SetAsync<string?>(_cacheKey, "rehydrated", Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
        await acquiredLock.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Rehydrate_adopts_a_fresh_inner_value_releases_the_lock_and_skips_the_generator()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var acquiredLock = AgedUntilLocked(_ => Entry("by-peer", TimeSpan.FromMinutes(9)));
        var generator = new CountingGenerator();

        await Sut.GetOrAddAsync(_cacheKey, generator.Run, RehydratePolicy(), token);

        await WaitForAsync(() => acquiredLock.ReceivedCalls().Any(), token);
        generator.Calls.Should().Be(0);
        _telemetry.Events.Should().Contain(e => e.Name == "cache.rehydrate.adopted");
        _telemetry.Events.Should().NotContain(e => e.Name == "cache.rehydrate.succeeded");
        await acquiredLock.Received(1).DisposeAsync();
        await _innerCache.DidNotReceive().SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("fresh", false)]
    [InlineData("throws", true)]
    [InlineData("failed", true)]
    [InlineData("absent", false)]
    [InlineData("unreadable", false)]
    public async Task Rehydrate_decides_on_the_snapshot_read_of_an_inner_cache_that_offers_one(string snapshot, bool fails)
    {
        var token = testContextAccessor.Current.CancellationToken;
        var proxy = DispatchProxy.Create<ICache, AdoptionProxy>();
        var inner = (AdoptionProxy)(object)proxy;
        _fixture.Inject(proxy);
        // The plain read always shows the aged copy, so only the snapshot read can tell a peer refreshed it.
        inner.Answer = (name, _) => name switch
        {
            nameof(ICache.GetCacheEntryAsync) => Entry("cached", TimeSpan.FromMinutes(2)),
            nameof(ICache.SetAsync) => true,
            _ => throw new NotSupportedException(name),
        };
        inner.Adoption = _ => snapshot switch
        {
            "fresh" => Entry("by-peer", TimeSpan.FromMinutes(9)),
            "throws" => throw new InvalidOperationException("lost"),
            "failed" => SnapshotOutcome.Failed,
            "absent" => SnapshotOutcome.Absent,
            _ => SnapshotOutcome.Unreadable,
        };
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        var generator = new CountingGenerator();

        await Sut.GetOrAddAsync(_cacheKey, generator.Run, RehydratePolicy(), token);

        await WaitForAsync(() => _telemetry.Events.Any(e => e.Name is "cache.rehydrate.adopted" or "cache.rehydrate.succeeded"), token);
        generator.Calls.Should().Be(snapshot == "fresh" ? 0 : 1);
        inner.Calls.Count(call => call.StartsWith("ReadSnapshotAsync:", StringComparison.Ordinal)).Should().Be(1);
        inner.Count(nameof(ICache.GetCacheEntryAsync)).Should().Be(1);
        _telemetry.Events.Where(e => e.Name == "cache.rehydrate.adopt_read_failed").Select(e => e.Properties!["reason"])
            .Should().Equal(fails ? ErrorReason : []);
    }

    [Theory]
    [InlineData(SnapshotOutcome.Found)]
    [InlineData(SnapshotOutcome.Absent)]
    [InlineData(SnapshotOutcome.Unreadable)]
    [InlineData(SnapshotOutcome.Failed)]
    public async Task A_snapshot_read_reports_the_inner_outcome_without_touching_the_local_copy(SnapshotOutcome outcome)
    {
        var proxy = DispatchProxy.Create<ICache, AdoptionProxy>();
        var inner = (AdoptionProxy)(object)proxy;
        _fixture.Inject(proxy);
        var held = Entry("inner", TimeSpan.FromMinutes(9));
        inner.Adoption = _ => outcome == SnapshotOutcome.Found ? held : outcome;

        var read = await Sut.ReadSnapshotAsync<string>(_cacheKey, null, testContextAccessor.Current.CancellationToken);

        read.Outcome.Should().Be(outcome);
        read.Entry.Should().BeSameAs(outcome == SnapshotOutcome.Found ? held : null);
        inner.Calls.Should().Equal("ReadSnapshotAsync:" + _cacheKey.Name);
    }

    [Theory]
    [InlineData(true, StoreOutcome.Committed)]
    [InlineData(false, StoreOutcome.Unknown)]
    public async Task An_inner_cache_that_is_not_migration_capable_commits_only_on_a_true_answer(bool answer, StoreOutcome expected)
    {
        var token = testContextAccessor.Current.CancellationToken;
        _fixture.Inject(_innerCache);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(answer);
        _innerCache.RemoveAsync<string>(_cacheKey, Arg.Any<CancellationToken>()).Returns(answer);

        (await Sut.WriteAsync(_cacheKey, "v", _clock.GetUtcNow().AddMinutes(5), null, token)).Should().Be(expected);
        (await Sut.DeleteAsync<string>(_cacheKey, token)).Should().Be(expected);
    }

    [Fact]
    public async Task Rehydrate_generates_when_the_inner_cache_offers_no_snapshot_read()
    {
        var token = testContextAccessor.Current.CancellationToken;
        // A fresh copy sits in the inner cache, but nothing guarantees its value and lifetime belong together.
        AgedUntilLocked(_ => Entry("by-peer", TimeSpan.FromMinutes(9)));
        _fixture.Inject(_innerCache);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(true);
        var generator = new CountingGenerator();

        await Sut.GetOrAddAsync(_cacheKey, generator.Run, RehydratePolicy(), token);

        await WaitForAsync(() => _telemetry.Events.Any(e => e.Name == "cache.rehydrate.succeeded"), token);
        generator.Calls.Should().Be(1);
        _telemetry.Events.Should().NotContain(e => e.Name == "cache.rehydrate.adopted" || e.Name == "cache.rehydrate.adopt_read_failed");
        await _innerCache.Received(1).GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rehydrate_adopts_a_null_marker_another_node_cached()
    {
        _options.CacheNullValues = true;
        var token = testContextAccessor.Current.CancellationToken;
        AgedUntilLocked(_ => new TestCacheEntry<string?> { Value = null, Expiration = _clock.GetUtcNow().AddMinutes(9), Found = true });
        var generator = new CountingGenerator();

        await Sut.GetOrAddAsync(_cacheKey, generator.Run, RehydratePolicy(), token);

        await WaitForAsync(() => _telemetry.Events.Any(e => e.Name == "cache.rehydrate.adopted"), token);
        generator.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("aged")]
    [InlineData("missing")]
    [InlineData("throws")]
    [InlineData("without_deadline")]
    public async Task Rehydrate_runs_the_generator_once_when_the_inner_value_is_not_fresh_enough(string inner)
    {
        var token = testContextAccessor.Current.CancellationToken;
        AgedUntilLocked(_ => inner switch
        {
            "aged" => Entry("cached", TimeSpan.FromMinutes(2)),
            "missing" => new TestCacheEntry<string?> { Value = null, Expiration = DateTimeOffset.MinValue, Found = false },
            "without_deadline" => new TestCacheEntry<string?> { Value = "no-ttl", Expiration = DateTimeOffset.MaxValue },
            _ => throw new InvalidOperationException("read failed"),
        });
        var generator = new CountingGenerator();
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, generator.Run, RehydratePolicy(), token);

        await WaitForAsync(() => _telemetry.Events.Any(e => e.Name == "cache.rehydrate.succeeded"), token);
        generator.Calls.Should().Be(1);
        _telemetry.Events.Should().NotContain(e => e.Name == "cache.rehydrate.adopted");
        _telemetry.Events.Where(e => e.Name == "cache.rehydrate.adopt_read_failed").Select(e => e.Properties!["reason"])
            .Should().Equal(inner == "throws" ? ErrorReason : []);
    }

    [Fact]
    public async Task Rehydrate_gives_the_inner_read_its_own_budget_and_generates_when_it_runs_out()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var short2s = RehydratePolicy(duration: TimeSpan.FromSeconds(2));
        // A read that never answers and ignores its token.
        var never = new TaskCompletionSource<ICacheEntry<string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        AgedUntilLockedAsync(_ => new ValueTask<ICacheEntry<string?>>(never.Task), agedRemaining: TimeSpan.FromMilliseconds(400));
        var generator = new CountingGenerator();
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, generator.Run, short2s, token);
        _clock.Advance(TimeSpan.FromMilliseconds(250));

        await WaitForAsync(() => _telemetry.Events.Any(e => e.Name == "cache.rehydrate.succeeded"), token);
        generator.Calls.Should().Be(1);
        _telemetry.Events.Should().NotContain(e => e.Name == "cache.rehydrate.timed_out");
        _telemetry.Events.Where(e => e.Name == "cache.rehydrate.adopt_read_failed").Select(e => e.Properties!["reason"]).Should().Equal("timeout");
    }

    [Theory]
    [InlineData(0.75, 150.0, false)]
    [InlineData(0.75, 150.001, true)]
    [InlineData(0.75, 149.0, false)]
    [InlineData(0.0, 600.0, false)]
    [InlineData(0.0, 601.0, true)]
    [InlineData(1.0, 0.0, false)]
    [InlineData(1.0, 0.001, true)]
    public void An_inner_copy_is_fresh_only_with_more_than_the_complement_of_the_threshold_left(double threshold, double remainingSeconds, bool expected)
    {
        var now = _clock.GetUtcNow();

        MultilayerCacheBase.InnerCopyFreshEnough(now + TimeSpan.FromSeconds(remainingSeconds), now, threshold, TimeSpan.FromSeconds(600)).Should().Be(expected);
    }

    [Fact]
    public void An_inner_copy_without_a_deadline_is_never_fresh()
    {
        MultilayerCacheBase.InnerCopyFreshEnough(DateTimeOffset.MaxValue, _clock.GetUtcNow(), 0.75, Duration).Should().BeFalse();
    }

    [Fact]
    public async Task Rehydrate_skipped_when_distributed_lock_unavailable()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Task<string?> Generator(CancellationToken _)
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
        }

        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(default(IAsyncDisposable));

        await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<string?>>)Generator, RehydratePolicy(), token);

        await WaitForAsync(() => _distributedLock.ReceivedCalls().Any(), token);
        generatorCalls.Should().Be(0);
        await _innerCache.DidNotReceive().SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rehydrate_does_not_release_lock_on_generator_failure()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Task<string?> Generator(CancellationToken _) => throw new InvalidOperationException("boom");

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        acquiredLock.DisposeAsync().Returns(_ => { _telemetry.Signal.Pulse(); return ValueTask.CompletedTask; });
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);

        await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<string?>>)Generator, RehydratePolicy(), token);

        await WaitForAsync(() => _distributedLock.ReceivedCalls().Any(), token);
        await acquiredLock.DidNotReceive().DisposeAsync();
        await _innerCache.DidNotReceive().SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caller_explicit_TimeSpan_drives_rehydrate_soft_TTL_not_policy()
    {
        // Entry has 2min remaining; caller passes 2min on this call. Elapsed fraction must be
        // computed against caller's TimeSpan (= 0, below threshold) rather than the policy's
        // 10min duration (which would yield 0.8, above the 0.75 threshold).
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Task<string?> Generator(CancellationToken _)
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
        }

        await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<string?>>)Generator, ttl, RehydratePolicy(), token);

        generatorCalls.Should().Be(0, "soft-TTL math must use caller's explicit TimeSpan, not the policy's DistributedExpiration");
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_run_timeout_that_ends_the_store_is_reported_as_timed_out_not_failed()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().AddMilliseconds(300) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<bool>(HangUntilCancelledAsync(call.Arg<CancellationToken>())));

        await Sut.GetOrAddAsync(_cacheKey, _ => Task.FromResult<string?>("rehydrated"), RehydratePolicy(duration: TimeSpan.FromSeconds(2)), token);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => _telemetry.Events.Any(e => e.Name is "cache.rehydrate.timed_out" or "cache.rehydrate.failed"), token);
        _telemetry.Events.Should().Contain(e => e.Name == "cache.rehydrate.timed_out");
        _telemetry.Events.Should().NotContain(e => e.Name == "cache.rehydrate.failed");
    }

    [Fact]
    public async Task Rehydrate_holds_lock_when_inner_write_returns_false()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Task<string?> Generator(CancellationToken _) => Task.FromResult<string?>("rehydrated");

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        acquiredLock.DisposeAsync().Returns(_ => { _telemetry.Signal.Pulse(); return ValueTask.CompletedTask; });
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        // Inner cache returns false from SetAsync — rehydrate must NOT report success.
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<string?>>)Generator, RehydratePolicy(), token);

        await WaitForAsync(() => _innerCache.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ICache.SetAsync)), token);
        // Lock stays held on failure so cooldown is enforced cluster-wide via the lock TTL.
        await acquiredLock.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task Rehydrate_disabled_when_policy_RehydrateEnabled_is_null_or_false()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Task<string?> Generator(CancellationToken _)
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
        }

        var policy = new CachePolicy
        {
            DistributedExpiration = Duration,
            RehydrateEnabled = false,
            Rehydrate = new RehydrateOptions { Threshold = 0.75 },
        };

        await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<string?>>)Generator, policy, token);

        generatorCalls.Should().Be(0);
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rehydrate_publishes_broadcast_so_other_nodes_invalidate_their_L1()
    {
        // Without the broadcast, peer nodes keep serving the pre-rehydrate L1 value until natural
        // expiry. The rehydrate write must publish a CacheSet event the same way a normal SetAsync does.
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Task<string?> Generator(CancellationToken _) => Task.FromResult<string?>("rehydrated");

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        acquiredLock.DisposeAsync().Returns(_ => { _telemetry.Signal.Pulse(); return ValueTask.CompletedTask; });
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<string?>>)Generator, RehydratePolicy(), token);

        await WaitForAsync(() => _topic.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ITopic<ICacheEvent>.PublishAsync)), token);
        await _topic.ReceivedWithAnyArgs(1).PublishAsync(default!, token);
    }

    [Fact]
    public async Task Rehydrate_skipped_when_current_entry_is_cached_null_and_CacheNullValues_true()
    {
        // Circuit-breaker A: the existing entry is a cached-null marker (a null value that was found),
        // and re-running the factory would just churn the same null, so the rehydrate must short-circuit
        // before taking the distributed lock.
        _options.CacheNullValues = true;
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var aged = new TestCacheEntry<string?> { Value = null, Found = true, Expiration = _clock.GetUtcNow().Add(ttl) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        var generatorCalls = 0;
        Task<string?> Generator(CancellationToken _)
        {
            Interlocked.Increment(ref generatorCalls);
            return Task.FromResult<string?>("rehydrated");
        }

        await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<string?>>)Generator, RehydratePolicy(), token);

        generatorCalls.Should().Be(0);
        await _distributedLock.DidNotReceiveWithAnyArgs().TryAcquireAsync(default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rehydrate_with_factory_returning_null_preserves_original_entry_expiration()
    {
        // Circuit-breaker B: when the factory transitions to null (upstream delete) and we cache nulls,
        // the rehydrate must write the null with the ORIGINAL entry deadline rather than extending
        // by a fresh TTL window — otherwise a stale null would survive forever.
        _options.CacheNullValues = true;
        var token = testContextAccessor.Current.CancellationToken;
        var ttl = TimeSpan.FromMinutes(2);
        var originalDeadline = _clock.GetUtcNow().Add(ttl);
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = originalDeadline };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);

        Task<string?> Generator(CancellationToken _) => Task.FromResult<string?>(null);

        var acquiredLock = Substitute.For<IAsyncDisposable>();
        acquiredLock.DisposeAsync().Returns(_ => { _telemetry.Signal.Pulse(); return ValueTask.CompletedTask; });
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(acquiredLock);
        _innerCache.SetAsync<string?>(_cacheKey, Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Sut.GetOrAddAsync(_cacheKey, (Func<CancellationToken, Task<string?>>)Generator, RehydratePolicy(), token);

        await WaitForAsync(() => _innerCache.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ICache.SetAsync)), token);
        await _innerCache.Received(1).SetAsync<string?>(
            _cacheKey,
            null,
            Arg.Is<DateTimeOffset>(d => Math.Abs((d - originalDeadline).TotalSeconds) < 1),
            Arg.Any<CachePolicy?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Single_key_rehydrate_does_not_emit_a_batch_size_tag()
    {
        var token = testContextAccessor.Current.CancellationToken;
        var aged = new TestCacheEntry<string?> { Value = "cached", Expiration = _clock.GetUtcNow().AddMinutes(2) };
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()).Returns(aged);
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());

        await Sut.GetOrAddAsync(_cacheKey, _ => Task.FromResult<string?>("rehydrated"), RehydratePolicy(), token);

        _telemetry.Events.Should().NotBeEmpty("the rehydrate path must still emit telemetry");
        _telemetry.Events.Should().AllSatisfy(e =>
            (e.Properties?.ContainsKey("batch.size") ?? false).Should().BeFalse(
                "single-key rehydrate telemetry must be unchanged by the set refactor"));
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _fixture.Inject<TimeProvider>(_clock);
        _cacheKey = _fixture.Create<string>();
        _topicKey = _fixture.Create<string>();
        _innerCache = _fixture.Freeze<ICache>();
        _fixture.Inject(AdoptionProxy.Over(_innerCache));
        _memoryCache = _fixture.Freeze<global::Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        _distributedLock = _fixture.Freeze<IDistributedLock>();
        _telemetry = new RecordingTelemetryProvider();
        _fixture.Inject<ICachingTelemetryProvider>(_telemetry);
        _options = new InMemoryRedisCacheOptions
        {
            DefaultExpiration = Duration,
            EntryFactory = new TestCacheEntryFactory(),
            LocalSingleFlightEnabled = false,
            DistributedLockEnabled = false,
        };
        _fixture.Inject<IMultilayerCacheOptions>(_options);
        _fixture.Inject<IMemoryCacheOptions>(_options);
        _cacheKeyStrategy = _fixture.Create<ICacheKeyStrategy>();
        _topicKeyStrategy = _fixture.Create<ITopicKeyStrategy>();
        _cacheKeyStrategy.GetCacheKey<string>(_cacheKey).Returns(_cacheKey);
        _topicKeyStrategy.GetTopicKey<string>().Returns(_topicKey);
        _topicFactory = _fixture.Freeze<ITopicFactory>();
        _topicProvider = _fixture.Freeze<MultilayerCachePerNameLockTests.ITopicProviderWithConnectionState>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _topicFactory.Get(Arg.Any<string>()).Returns(_topicProvider);
        _topicProvider.Create(_topicKey).Returns(_topic);
        _topic.PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(_ => true);
        _memoryCacheFactory = _fixture.Freeze<global::UiPath.Caching.IMemoryCacheFactory>();
        _memoryCacheFactory.Get(Arg.Any<IMemoryCacheOptions>()).Returns(_memoryCache);
        _fixture.Freeze<ICacheEventFactory>();
        return ValueTask.CompletedTask;
    }

    private static CachePolicy RehydratePolicy(double threshold = 0.75, TimeSpan? duration = null) => new()
    {
        DistributedExpiration = duration ?? Duration,
        RehydrateEnabled = true,
        Rehydrate = new RehydrateOptions
        {
            Threshold = threshold,
            BaseCooldown = TimeSpan.FromSeconds(1),
            MaxCooldown = TimeSpan.FromMinutes(5),
            TimeoutFraction = 0.5,
            Name = "test-profile",
        },
    };

    private static async Task<bool> HangUntilCancelledAsync(CancellationToken token)
    {
        await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token);
        return true;
    }

    private Task WaitForAsync(Func<bool> predicate, CancellationToken token) =>
        _telemetry.Signal.UntilAsync(predicate, token);

    private TestCacheEntry<string?> Entry(string? value, TimeSpan remaining) => new() { Value = value, Expiration = _clock.GetUtcNow().Add(remaining) };

    /// <summary>The caller's read finds a copy past the threshold; every read after the rehydrate lock was won answers <paramref name="afterLock"/>, so the stub does not count reads.</summary>
    private IAsyncDisposable AgedUntilLocked(Func<NSubstitute.Core.CallInfo, ICacheEntry<string?>> afterLock, TimeSpan? agedRemaining = null) =>
        AgedUntilLockedAsync(call => new ValueTask<ICacheEntry<string?>>(afterLock(call)), agedRemaining);

    private IAsyncDisposable AgedUntilLockedAsync(Func<NSubstitute.Core.CallInfo, ValueTask<ICacheEntry<string?>>> afterLock, TimeSpan? agedRemaining = null)
    {
        var aged = Entry("cached", agedRemaining ?? TimeSpan.FromMinutes(2));
        var acquiredLock = Substitute.For<IAsyncDisposable>();
        acquiredLock.DisposeAsync().Returns(_ => { _telemetry.Signal.Pulse(); return ValueTask.CompletedTask; });
        var locked = 0;
        _distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncDisposable?>(_ =>
            {
                Volatile.Write(ref locked, 1);
                return acquiredLock;
            });
        _innerCache.GetCacheEntryAsync<string>(_cacheKey, Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<ICacheEntry<string?>>>(async call => Volatile.Read(ref locked) == 0 ? aged : await afterLock(call).ConfigureAwait(false));
        return acquiredLock;
    }

    private sealed class CountingGenerator
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

#pragma warning disable S1172 // Used as a method group for a generator delegate, so the signature is fixed.
        public Task<string?> Run(CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<string?>("rehydrated");
        }
#pragma warning restore S1172
    }
}
