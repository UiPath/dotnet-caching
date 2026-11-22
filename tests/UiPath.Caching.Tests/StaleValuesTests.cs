using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace UiPath.Caching.Tests;

/// <summary>The kept values on their own, in a local tier of twenty: a drop meant for one value never takes another, and a value takes its entry's size only once that entry has expired.</summary>
public sealed class StaleValuesTests : IDisposable
{
    private const string Name = "k";
    private const int Limit = 20;
    private const long Size = 3;
    private static readonly TimeSpan Age = TimeSpan.FromHours(1);
    private static readonly TopicKey Topic = new("t");

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly CacheEntryFactory _entries = new();
    private readonly ITopicProvider _topics = Substitute.For<ITopicProvider>();
    private readonly InMemoryCacheOptions _options = new() { SizeLimit = Limit };
    private readonly MemoryCache _local;
    private IObserver<ICacheEvent>? _watcher;

    public StaleValuesTests()
    {
        _local = (MemoryCache)new MemoryCacheFactory(_clock, NullLoggerFactory.Instance).Get(_options);
        var topic = Substitute.For<ITopic<ICacheEvent>>();
        topic.Subscribe(Arg.Do<IObserver<ICacheEvent>>(watcher => _watcher = watcher)).Returns(Substitute.For<IDisposable>());
        _topics.Create(Topic).Returns(topic);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private long SizeHeld => _local.GetCurrentStatistics()!.CurrentEstimatedSize!.Value;

    private DateTimeOffset Deadline => _clock.GetUtcNow().AddMinutes(1);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_drop_for_an_older_value_racing_a_keep_never_takes_the_newer_value(bool byEntry)
    {
        using var store = Store();
        var deadline = Deadline;
        var lost = 0;
        for (var round = 0; round < 20000; round++)
        {
            var old = _entries.Create<string?>("old", deadline);
            var newest = _entries.Create<string?>("newest", deadline);
            var oldValue = store.Keep(Name, Topic, old, deadline, Size, Age, store.EpochFor(Topic), store.ChangeFor(Name))!;

            // As the token of the entry a write replaces changes, or a late answer drops the value it replaced, while that write keeps its own.
            var go = 0;
            var dropping = Task.Run(
                () =>
                {
                    SpinWait.SpinUntil(() => Volatile.Read(ref go) == 1);
                    if (byEntry)
                    {
                        store.Drop(Name, old);
                    }
                    else
                    {
                        store.Drop(Name, oldValue);
                    }
                },
                Ct);
            var keeping = Task.Run(
                () =>
                {
                    SpinWait.SpinUntil(() => Volatile.Read(ref go) == 1);
                    store.Keep(Name, Topic, newest, deadline, Size, Age, store.EpochFor(Topic), store.ChangeFor(Name));
                },
                Ct);
            Volatile.Write(ref go, 1);
            await Task.WhenAll(dropping, keeping);

            if (!store.TryGet<string>(Name, Age, out _, out var kept) || !ReferenceEquals(kept, newest))
            {
                lost++;
            }
        }

        lost.Should().Be(0, "the drop was for the older value");
    }

    [Fact]
    public async Task A_removal_racing_the_charge_of_an_expired_entrys_value_wins()
    {
        using var store = Store();
        var left = 0;
        for (var round = 0; round < 20000; round++)
        {
            var entry = Entry("v");
            Keep(store, entry).Should().NotBeNull();

            // As a peer's removal of the key is heard while the tier tells that the entry expired.
            var go = 0;
            var charging = Task.Run(
                () =>
                {
                    SpinWait.SpinUntil(() => Volatile.Read(ref go) == 1);
                    Leaves(store, entry, EvictionReason.Expired);
                },
                Ct);
            var removing = Task.Run(
                () =>
                {
                    SpinWait.SpinUntil(() => Volatile.Read(ref go) == 1);
                    store.Drop(Name);
                },
                Ct);
            Volatile.Write(ref go, 1);
            await Task.WhenAll(charging, removing);

            if (Kept(store) is not null)
            {
                left++;
                store.Drop(Name);
            }
        }

        left.Should().Be(0, "a removed value is never kept");
    }

    [Fact]
    public async Task A_read_never_misses_a_value_the_tier_is_storing_again()
    {
        using var store = Store();

        // At no size, as a value is until its entry expires: the scan then has nothing to subtract from what the tier counts.
        StaleValue? KeepUnsized(UiPath.Caching.ICacheEntry entry) => store.Keep(Name, Topic, entry, Deadline, 0, Age, store.EpochFor(Topic), store.ChangeFor(Name));
        KeepUnsized(Entry("first")).Should().NotBeNull();
        var done = 0;
        var missed = 0;

        // As the tier's scan for expired entries, which a miss after an expiry starts: it takes out an entry that is being replaced.
        var scanning = Task.Run(
            () =>
            {
                while (Volatile.Read(ref done) == 0)
                {
                    _local.Compact(0);
                }
            },
            Ct);
        var reading = Task.Run(
            () =>
            {
                while (Volatile.Read(ref done) == 0)
                {
                    if (Kept(store) is null)
                    {
                        missed++;
                    }
                }
            },
            Ct);
        for (var round = 0; round < 200000; round++)
        {
            var entry = Entry("v");
            KeepUnsized(entry);
            Leaves(store, entry, EvictionReason.Expired);
        }

        Volatile.Write(ref done, 1);
        await Task.WhenAll(scanning, reading);

        missed.Should().Be(0, "the key holds a value from the first keep on");
    }

    [Fact]
    public void A_dropped_value_is_neither_served_nor_current_and_a_later_keep_replaces_it()
    {
        using var store = Store();
        var old = Entry("old");
        var value = Keep(store, old)!;

        store.Drop(Name, old);

        store.TryGet<string>(Name, Age, out _, out _).Should().BeFalse();
        store.IsCurrent(Name, value).Should().BeFalse();
        var newest = Entry("newest");
        Keep(store, newest).Should().NotBeNull();
        Kept(store).Should().BeSameAs(newest);
    }

    [Fact]
    public void A_kept_value_takes_its_entrys_size_only_once_that_entry_has_expired()
    {
        using var store = Store();
        var entry = Entry("v");

        Keep(store, entry).Should().NotBeNull();

        SizeHeld.Should().Be(0, "the local entry holds the same payload");
        Leaves(store, entry, EvictionReason.Expired);
        SizeHeld.Should().Be(Size, "the size the tier charged the entry");
        Kept(store).Should().BeSameAs(entry);
    }

    [Theory]
    [InlineData("removal")]
    [InlineData("write")]
    [InlineData("gap")]
    public void What_lands_between_an_entrys_expiry_and_the_charge_of_its_kept_value_wins(string landing)
    {
        using var store = Store();
        var old = Entry("old");
        Keep(store, old).Should().NotBeNull();
        var newest = Entry("newest");
        switch (landing)
        {
            case "removal":
                store.Drop(Name);
                break;
            case "write":
                Keep(store, newest).Should().NotBeNull();
                break;
            default:
                ((IMissedEventsObserver)_watcher!).OnEventsMissed(MissedEventsReason.SubscriptionGap);
                break;
        }

        Leaves(store, old, EvictionReason.Expired);

        SizeHeld.Should().Be(0, "the expired entry's value is not kept, and the newer write's entry is live");
        _local.Count.Should().Be(landing == "write" ? 1 : 0);
        Kept(store).Should().BeSameAs(landing == "write" ? newest : null);
    }

    [Fact]
    public void An_entry_the_tier_evicts_takes_its_kept_value_with_it_and_no_newer_one()
    {
        using var store = Store();
        var old = Entry("old");
        Keep(store, old).Should().NotBeNull();

        Leaves(store, old, EvictionReason.Capacity);

        Kept(store).Should().BeNull("nothing would give the value a size later");
        var older = Entry("older");
        var newest = Entry("newest");
        Keep(store, older).Should().NotBeNull();
        Keep(store, newest).Should().NotBeNull();
        Leaves(store, older, EvictionReason.Capacity);
        Kept(store).Should().BeSameAs(newest);
    }

    [Fact]
    public void A_replaced_entry_leaves_its_kept_value_to_the_write_that_replaced_it()
    {
        using var store = Store();
        var entry = Entry("v");
        Keep(store, entry).Should().NotBeNull();

        Leaves(store, entry, EvictionReason.Replaced);

        Kept(store).Should().BeSameAs(entry);
        SizeHeld.Should().Be(0);
    }

    [Fact]
    public void A_kept_value_the_full_tier_refuses_is_not_kept()
    {
        using var store = Store();
        var entry = Entry("v");
        Keep(store, entry).Should().NotBeNull();
        for (var i = 0; i < Limit; i++)
        {
            _local.Set($"live{i}", i, new MemoryCacheEntryOptions { Size = 1 });
        }

        Leaves(store, entry, EvictionReason.Expired);

        Kept(store).Should().BeNull();
        SizeHeld.Should().BeLessThanOrEqualTo(Limit);
    }

    [Fact]
    public void A_kept_value_the_tier_fails_to_charge_is_not_kept()
    {
        var failing = new FailingMemory(_local);
        using var store = Store(failing);
        var entry = Entry("v");
        Keep(store, entry).Should().NotBeNull();
        failing.Fails = true;

        var leaving = () => Leaves(store, entry, EvictionReason.Expired);

        leaving.Should().Throw<InvalidOperationException>("the tier reports it, as it does any failure of an eviction callback");
        _local.Count.Should().Be(0, "a value with no size and no entry would stay, uncounted, to the end of its stale age");
    }

    [Fact]
    public void A_write_the_tier_fails_to_keep_leaves_no_older_value_behind()
    {
        var failing = new FailingMemory(_local);
        using var store = Store(failing);
        Keep(store, Entry("old")).Should().NotBeNull();
        failing.Fails = true;

        var keeping = () => Keep(store, Entry("newest"));

        keeping.Should().Throw<InvalidOperationException>();
        _local.Count.Should().Be(0, "the older value's entry was replaced by the write");
    }

    [Fact]
    public void A_value_kept_before_events_were_lost_is_not_served_and_leaves_at_the_first_look()
    {
        using var store = Store();
        Keep(store, Entry("v")).Should().NotBeNull();

        ((IMissedEventsObserver)_watcher!).OnEventsMissed(MissedEventsReason.Lost);

        store.Find(Name).Should().BeNull();
        _local.Count.Should().Be(0);
    }

    [Fact]
    public void An_entry_that_leaves_a_disposed_tier_is_let_go()
    {
        using var store = Store();
        var entry = Entry("v");
        Keep(store, entry).Should().NotBeNull();
        _local.Dispose();

        var leaving = () => Leaves(store, entry, EvictionReason.Expired);

        leaving.Should().NotThrow();
    }

    [Fact]
    public void A_value_kept_after_its_entry_expired_is_kept_at_the_entrys_size()
    {
        using var store = Store();
        var entry = Entry("v1");

        // The keep runs late, as a starved continuation does: the entry's expiry was heard first, and found nothing to charge.
        _clock.Advance(TimeSpan.FromMinutes(2));
        Leaves(store, entry, EvictionReason.Expired);
        var kept = store.Keep(Name, Topic, entry, _clock.GetUtcNow().AddMinutes(-1), Size, Age, store.EpochFor(Topic), store.ChangeFor(Name))!;
        store.Settle(Name, kept, entry, local: false, custom: false, changed: false);

        Kept(store).Should().BeSameAs(entry, "an entry that only expired before the keep leaves its value to fail-safe");
        SizeHeld.Should().Be(Size, "the value took over the expired entry's size");
    }

    [Theory]
    [InlineData(EvictionReason.Removed)]
    [InlineData(EvictionReason.Replaced)]
    [InlineData(EvictionReason.TokenExpired)]
    [InlineData(EvictionReason.Capacity)]
    public void A_value_whose_entry_was_taken_before_the_keep_is_dropped(EvictionReason reason)
    {
        using var store = Store();
        var entry = Entry("v1");

        // A peer's removal or invalidation, a newer write, or the tier's compaction took the entry between the commit and the keep.
        Leaves(store, entry, reason);
        var kept = Keep(store, entry)!;
        store.Settle(Name, kept, entry, local: false, custom: false, changed: false);

        Kept(store).Should().BeNull();
    }

    [Theory]
    [InlineData(EvictionReason.Removed, false)]
    [InlineData(EvictionReason.Expired, true)]
    public void A_value_whose_entry_is_not_yet_heard_leaving_waits_for_that_leaving(EvictionReason reason, bool stays)
    {
        using var store = Store();
        var entry = Entry("v1");
        var kept = Keep(store, entry)!;

        store.Settle(Name, kept, entry, local: false, custom: false, changed: false);
        Kept(store).Should().BeSameAs(entry, "nothing is known yet about why the entry is gone");

        // A peer's removal heard after the commit still drops the value; its own expiry charges it.
        Leaves(store, entry, reason);
        (Kept(store) is not null).Should().Be(stays);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_peers_change_heard_after_the_entry_expired_and_before_the_keep_refuses_the_keep(bool sizeLimited)
    {
        using var local = sizeLimited ? null : UnlimitedTier();
        using var store = Store(local, sizeLimited ? null : new InMemoryCacheOptions());
        var entry = Entry("v1");
        var epoch = store.EpochFor(Topic);
        var change = store.ChangeFor(Name);

        // The write read the generation, committed, and its entry expired; its token was disposed with it, so only the store hears the peer.
        _clock.Advance(TimeSpan.FromMinutes(2));
        if (sizeLimited)
        {
            Leaves(store, entry, EvictionReason.Expired);
        }

        PeerChanges();
        var kept = store.Keep(Name, Topic, entry, _clock.GetUtcNow().AddMinutes(-1), Size, Age, epoch, change);

        kept.Should().BeNull("the peer's change came after the value was read, even with nothing kept yet");
        Kept(store).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_value_whose_entry_expired_before_a_late_keep_with_no_peer_change_is_kept(bool sizeLimited)
    {
        using var local = sizeLimited ? null : UnlimitedTier();
        using var store = Store(local, sizeLimited ? null : new InMemoryCacheOptions());
        var entry = Entry("v1");
        var epoch = store.EpochFor(Topic);
        var change = store.ChangeFor(Name);

        _clock.Advance(TimeSpan.FromMinutes(2));
        if (sizeLimited)
        {
            Leaves(store, entry, EvictionReason.Expired);
        }

        var kept = store.Keep(Name, Topic, entry, _clock.GetUtcNow().AddMinutes(-1), Size, Age, epoch, change)!;
        store.Settle(Name, kept, entry, local: false, custom: false, changed: false);

        Kept(store).Should().BeSameAs(entry);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(EvictionReason.Expired)]
    public void An_entry_the_caches_factory_did_not_make_is_dropped_once_it_is_not_local(EvictionReason? heard)
    {
        using var store = Store();
        var entry = Entry("v1");
        if (heard is { } reason)
        {
            Leaves(store, entry, reason);
        }

        var kept = Keep(store, entry)!;
        store.Settle(Name, kept, entry, local: false, custom: true, changed: false);

        Kept(store).Should().BeNull("its token cannot be told apart from one a peer's change fired, so the old rule holds");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_value_whose_entrys_token_fired_is_dropped_even_when_the_entry_is_local(bool local)
    {
        using var store = Store();
        var entry = Entry("v1");
        var kept = Keep(store, entry)!;

        // The store heard the peer before the write read the generation; only the entry's token heard it after.
        store.Settle(Name, kept, entry, local, custom: false, changed: true);

        Kept(store).Should().BeNull();
    }

    [Fact]
    public void A_value_whose_entry_is_still_local_is_left_as_kept()
    {
        using var store = Store();
        var entry = Entry("v1");
        var kept = Keep(store, entry)!;

        store.Settle(Name, kept, entry, local: true, custom: false, changed: false);

        Kept(store).Should().BeSameAs(entry);
        SizeHeld.Should().Be(0, "the local entry still holds the payload");
    }

    public void Dispose() => _local.Dispose();

    private static void Leaves(StaleValues store, UiPath.Caching.ICacheEntry entry, EvictionReason reason) => StaleValues.EntryLeft(Name, entry, reason, store);

    private static UiPath.Caching.ICacheEntry<string?>? Kept(StaleValues store) => store.TryGet<string>(Name, Age, out _, out var kept) ? kept : null;

    private UiPath.Caching.ICacheEntry<string?> Entry(string value) => _entries.Create<string?>(value, Deadline);

    private StaleValue? Keep(StaleValues store, UiPath.Caching.ICacheEntry entry) => store.Keep(Name, Topic, entry, Deadline, Size, Age, store.EpochFor(Topic), store.ChangeFor(Name));

    private StaleValues Store(IMemoryCache? local = null, InMemoryCacheOptions? options = null) => new(_topics, null, _clock, options ?? _options, local ?? _local, NullLogger.Instance);

    private MemoryCache UnlimitedTier() => (MemoryCache)new MemoryCacheFactory(_clock, NullLoggerFactory.Instance).Get(new InMemoryCacheOptions());

    private void PeerChanges() => _watcher!.OnNext(new CacheEvent { Id = "e1", Type = KnownEventTypes.CacheRemoved, Source = new Uri("urn:peer"), Data = new CacheEventData(Name) });

    /// <summary>The local tier, which takes no new entry while <see cref="Fails"/> is set.</summary>
    private sealed class FailingMemory(IMemoryCache inner) : IMemoryCache
    {
        public bool Fails { get; set; }

        public Microsoft.Extensions.Caching.Memory.ICacheEntry CreateEntry(object key) => Fails ? throw new InvalidOperationException("no entry") : inner.CreateEntry(key);

        public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);

        public void Remove(object key) => inner.Remove(key);

        public void Dispose()
        {
        }
    }
}
