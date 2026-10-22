#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Broadcast;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>The order of a set's value and event, and the opt-in background write.</summary>
public class MultilayerCacheBackgroundWriteTests
{
    private static readonly CachePolicy Awaited = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };

    private static readonly CachePolicy Background = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30), BackgroundDistributedWrite = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_set_writes_the_value_before_it_publishes_the_event()
    {
        using var h = new Harness();

        (await h.Cache.SetAsync<string>("k", "v", Awaited, Ct)).Should().BeTrue();

        h.Log.Should().Equal("write:k=v", "event:k");
        h.WrittenWhenPublished.Should().Equal(true);
    }

    [Fact]
    public async Task A_set_the_inner_tier_refuses_publishes_nothing_and_returns_false()
    {
        using var h = new Harness();
        h.OnWrite = (_, _) => Task.FromResult(false);

        (await h.Cache.SetAsync<string>("k", "v", Awaited, Ct)).Should().BeFalse();

        h.Log.Should().Equal("write:k=v");
    }

    [Fact]
    public async Task A_set_whose_write_throws_publishes_nothing_and_returns_false()
    {
        using var h = new Harness();
        h.OnWrite = (_, _) => throw new InvalidOperationException("redis is down");

        (await h.Cache.SetAsync<string>("k", "v", Awaited, Ct)).Should().BeFalse();

        h.Log.Should().Equal("write:k=v");
    }

    [Fact]
    public async Task A_set_cancelled_before_its_write_lands_publishes_nothing_and_returns_false()
    {
        using var h = new Harness();
        using var cancelled = new CancellationTokenSource();
        h.OnWrite = (_, token) =>
        {
            cancelled.Cancel();
            throw new OperationCanceledException(token);
        };

        (await h.Cache.SetAsync<string>("k", "v", Awaited, cancelled.Token)).Should().BeFalse();

        h.Log.Should().NotContain("event:k");
    }

    [Fact]
    public async Task A_set_the_caller_cancels_while_the_write_runs_still_publishes_what_was_written()
    {
        using var h = new Harness();
        using var cancelled = new CancellationTokenSource();
        h.OnWrite = (_, _) =>
        {
            cancelled.Cancel();
            return Task.FromResult(true);
        };

        (await h.Cache.SetAsync<string>("k", "v", Awaited, cancelled.Token)).Should().BeTrue();

        h.Log.Should().Equal("write:k=v", "event:k");
    }

    [Fact]
    public async Task A_set_whose_event_is_refused_returns_false_with_the_value_written_and_kept()
    {
        using var h = new Harness();
        h.OnPublish = _ => new ValueTask<bool>(false);

        (await h.Cache.SetAsync<string>("k", "v", Awaited, Ct)).Should().BeFalse();

        h.Log.Should().Equal("write:k=v", "event:k");
        h.Local("k").Should().Be("v");
    }

    [Fact]
    public async Task A_set_whose_event_throws_returns_false_instead_of_throwing()
    {
        using var h = new Harness();
        h.OnPublish = _ => throw new InvalidOperationException("stream is gone");

        (await h.Cache.SetAsync<string>("k", "v", Awaited, Ct)).Should().BeFalse();

        h.Log.Should().Equal("write:k=v", "event:k");
    }

    [Fact]
    public async Task A_set_the_inner_tier_took_is_still_published_when_the_local_tier_fails()
    {
        using var h = new Harness(failingMemory: true);

        (await h.Cache.SetAsync<string>("k", "v", Awaited, Ct)).Should().BeFalse();

        h.Log.Should().Equal("write:k=v", "event:k");
    }

    [Fact]
    public async Task A_background_set_returns_with_the_value_local_and_the_write_already_issued_but_not_finished()
    {
        using var h = new Harness();
        var gate = h.Gate("k");

        (await h.Cache.SetAsync<string>("k", "v", Background, Ct)).Should().BeTrue();

        h.Log.Should().Equal("write:k=v");
        h.Local("k").Should().Be("v");
        gate.SetResult(true);
        await h.SettledAsync();
        h.Log.Should().Equal("write:k=v", "event:k");
        h.WrittenWhenPublished.Should().Equal(true);
    }

    [Fact]
    public async Task A_background_set_is_local_for_the_disconnected_cap_while_the_write_is_pending_and_for_the_normal_lifetime_once_it_lands()
    {
        using var h = new Harness();
        h.OnWrite = (_, _) => Task.FromResult(true);

        await h.Cache.SetAsync<string>("k", "v", Background, Ct);
        await h.SettledAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        h.Local("k").Should().Be("v", "the landed write promoted it past the 30 s cap");
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        h.Local("k").Should().BeNull();
    }

    [Fact]
    public async Task A_background_set_the_inner_tier_refuses_stays_local_for_the_cap_only_and_sends_no_event()
    {
        using var h = new Harness();
        h.OnWrite = (_, _) => Task.FromResult(false);

        (await h.Cache.SetAsync<string>("k", "v", Background, Ct)).Should().BeTrue();
        await h.SettledAsync();

        h.Log.Should().Equal("write:k=v");
        h.Clock.Advance(TimeSpan.FromSeconds(29));
        h.Local("k").Should().Be("v");
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Local("k").Should().BeNull();
        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());
    }

    [Fact]
    public async Task A_background_set_whose_write_throws_is_observed_counted_and_stays_local_for_the_cap_only()
    {
        using var h = new Harness();
        h.OnWrite = (_, _) => throw new InvalidOperationException("redis is down");

        (await h.Cache.SetAsync<string>("k", "v", Background, Ct)).Should().BeTrue();
        await h.SettledAsync();

        h.Log.Should().Equal("write:k=v");
        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        h.Local("k").Should().BeNull();
    }

    [Fact]
    public async Task A_background_set_whose_event_is_refused_is_counted_and_the_value_is_still_promoted()
    {
        using var h = new Harness();
        h.OnPublish = _ => new ValueTask<bool>(false);

        (await h.Cache.SetAsync<string>("k", "v", Background, Ct)).Should().BeTrue();
        await h.SettledAsync();

        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Local("k").Should().Be("v");
    }

    [Fact]
    public async Task A_background_set_whose_event_throws_is_observed()
    {
        using var h = new Harness();
        h.OnPublish = _ => throw new InvalidOperationException("stream is gone");

        (await h.Cache.SetAsync<string>("k", "v", Background, Ct)).Should().BeTrue();
        await h.SettledAsync();

        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());
    }

    [Fact]
    public async Task A_promotion_does_not_overwrite_a_newer_local_value()
    {
        using var h = new Harness();
        var first = h.Gate("k", "a");
        h.Gate("k", "b");

        await h.Cache.SetAsync<string>("k", "a", Background, Ct);
        await h.Cache.SetAsync<string>("k", "b", Background, Ct);
        first.SetResult(true);
        await h.UntilAsync(() => h.Log.Contains("event:k"));

        h.Local("k").Should().Be("b");
        h.ReleaseAll();
        await h.SettledAsync();
        h.Local("k").Should().Be("b");
    }

    [Fact]
    public async Task A_remove_after_a_background_set_waits_for_it_and_leaves_nothing_local()
    {
        using var h = new Harness();
        var gate = h.Gate("k");
        await h.Cache.SetAsync<string>("k", "v", Background, Ct);

        var remove = h.Cache.RemoveAsync<string>("k", Ct).AsTask();

        await Task.Delay(100, Ct);
        remove.IsCompleted.Should().BeFalse("the unlink must not overtake the write it follows");
        h.Log.Should().Equal("write:k=v");
        gate.SetResult(true);
        (await remove).Should().BeTrue();
        await h.SettledAsync();
        h.Log.Should().Equal("write:k=v", "event:k", "remove:k", "event:k");
        h.Local("k").Should().BeNull("the promotion of the set found the entry gone");
    }

    [Fact]
    public async Task Background_sets_of_one_key_reach_the_inner_tier_in_the_order_they_were_issued()
    {
        using var h = new Harness();
        var first = h.Gate("k", "a");

        await h.Cache.SetAsync<string>("k", "a", Background, Ct);
        await h.Cache.SetAsync<string>("k", "b", Background, Ct);

        h.Log.Should().Equal("write:k=a");
        first.SetResult(true);
        await h.SettledAsync();
        h.Log.Should().Equal("write:k=a", "event:k", "write:k=b", "event:k");
    }

    [Fact]
    public async Task A_background_set_waits_for_an_earlier_set_that_found_no_free_place()
    {
        using var h = new Harness(new InMemoryRedisCacheOptions { MaxBackgroundWrites = 1 });
        var slot = h.Gate("other");
        var foregroundWrite = h.Gate("k", "w");
        await h.Cache.SetAsync<string>("other", "x", Background, Ct);
        var foreground = h.Cache.SetAsync<string>("k", "w", Background, Ct).AsTask();
        await h.UntilAsync(() => h.Log.Contains("write:k=w"));
        slot.SetResult(true);
        await h.UntilAsync(() => h.Cache.RunsInFlight == 0);

        await h.Cache.SetAsync<string>("k", "b", Background, Ct);

        await Task.Delay(100, Ct);
        h.Log.Should().NotContain("write:k=b", "the set before it is still writing the key");
        foregroundWrite.SetResult(true);
        (await foreground).Should().BeTrue();
        await h.SettledAsync();
        h.Log.Where(e => e.StartsWith("write:k=", StringComparison.Ordinal)).Should().Equal("write:k=w", "write:k=b");
    }

    [Fact]
    public async Task A_background_set_waits_for_an_earlier_remove_of_the_key()
    {
        using var h = new Harness();
        await h.ActivateAsync();
        var removing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.OnRemove = _ => removing.Task;
        var remove = h.Cache.RemoveAsync<string>("k", Ct).AsTask();
        await h.UntilAsync(() => h.Log.Contains("remove:k"));

        await h.Cache.SetAsync<string>("k", "b", Background, Ct);

        await Task.Delay(100, Ct);
        h.Log.Should().NotContain("write:k=b", "the remove landing after it would leave the inner tier without the value");
        removing.SetResult(true);
        await remove;
        await h.SettledAsync();
        h.Log.Where(e => !e.StartsWith("event:", StringComparison.Ordinal)).Should().Equal("remove:k", "write:k=b");
        h.Local("k").Should().Be("b");
    }

    [Fact]
    public async Task A_try_add_waits_for_a_background_set_of_the_key_and_then_loses_to_it()
    {
        using var h = new Harness();
        var first = h.Gate("k", "a");
        await h.Cache.SetAsync<string>("k", "a", Background, Ct);
        h.Clock.Advance(TimeSpan.FromSeconds(31));

        var add = h.Cache.TryAddAsync<string>("k", "b", Awaited, Ct).AsTask();

        await Task.Delay(100, Ct);
        h.Log.Should().NotContain("tryadd:k=b", "the queued set would overwrite what the add claims");
        first.SetResult(true);
        (await add).Should().BeFalse();
        await h.SettledAsync();
        h.Log.Where(e => !e.StartsWith("event:", StringComparison.Ordinal)).Should().Equal("write:k=a", "tryadd:k=b");
    }

    [Fact]
    public async Task A_refresh_waits_for_a_background_set_of_the_key()
    {
        using var h = new Harness();
        var first = h.Gate("k", "a");
        await h.Cache.SetAsync<string>("k", "a", Background, Ct);

        var refresh = h.Cache.RefreshAsync<string>("k", Awaited, Ct).AsTask();

        await Task.Delay(100, Ct);
        h.Log.Should().NotContain("refresh:k");
        first.SetResult(true);
        await refresh;
        await h.SettledAsync();
        h.Log.Where(e => !e.StartsWith("event:", StringComparison.Ordinal)).Should().Equal("write:k=a", "refresh:k");
    }

    [Fact]
    public async Task An_awaited_set_landing_after_a_newer_background_set_leaves_the_newer_value_local()
    {
        using var h = new Harness();
        await h.ActivateAsync();
        var first = h.Gate("k", "v1");
        var awaited = h.Cache.SetAsync<string>("k", "v1", Awaited, Ct).AsTask();
        await h.UntilAsync(() => h.Log.Contains("write:k=v1"));

        await h.Cache.SetAsync<string>("k", "v2", Background, Ct);
        h.Local("k").Should().Be("v2");
        first.SetResult(true);
        (await awaited).Should().BeTrue();
        await h.SettledAsync();

        h.Local("k").Should().Be("v2", "the awaited write landed first in the inner tier, so the background one holds it");
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Local("k").Should().Be("v2", "the background write's landing promoted it past the 30 s cap");
    }

    [Fact]
    public async Task A_remove_waiting_behind_a_background_set_ends_when_its_token_is_cancelled_and_frees_its_place()
    {
        var logger = new RecordingLogger();
        using var h = new Harness(logger: logger);
        var first = h.Gate("k", "a");
        await h.Cache.SetAsync<string>("k", "a", Background, Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var remove = h.Cache.RemoveAsync<string>("k", cancel.Token).AsTask();
        await Task.Delay(100, Ct);
        await cancel.CancelAsync();

        (await remove.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Should().BeFalse();
        h.Log.Should().NotContain("remove:k");
        logger.Records.Should().NotContain(r => r.Level >= LogLevel.Warning, "the caller cancelled; nothing failed");
        await h.Cache.SetAsync<string>("k", "c", Background, Ct);
        first.SetResult(true);
        await h.SettledAsync();
        h.Log.Where(e => e.StartsWith("write:", StringComparison.Ordinal)).Should().Equal("write:k=a", "write:k=c");
    }

    [Fact]
    public async Task A_try_add_whose_event_is_slow_leaves_a_background_set_issued_meanwhile_local()
    {
        using var h = new Harness();
        var publishing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        h.OnPublish = _ => Interlocked.Increment(ref first) == 1 ? new ValueTask<bool>(publishing.Task) : new ValueTask<bool>(true);
        var add = h.Cache.TryAddAsync<string>("k", "v1", Awaited, Ct).AsTask();
        await h.UntilAsync(() => Volatile.Read(ref first) == 1);

        await h.Cache.SetAsync<string>("k", "v2", Background, Ct);
        publishing.SetResult(true);
        (await add).Should().BeTrue();
        await h.SettledAsync();

        h.Local("k").Should().Be("v2", "the set was issued after the add won, so its value is the newer one");
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Local("k").Should().Be("v2");
    }

    [Fact]
    public async Task A_read_of_the_inner_tier_landing_after_a_background_set_leaves_the_set_value_local()
    {
        using var h = new Harness();
        var reading = new TaskCompletionSource<ICacheEntry<string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        h.OnRead = _ => Interlocked.Increment(ref started) == 1 ? reading.Task : null;
        var read = h.Cache.GetAsync<string>("k", Awaited, Ct).AsTask();
        await h.UntilAsync(() => Volatile.Read(ref started) == 1);

        await h.Cache.SetAsync<string>("k", "v2", Background, Ct);
        reading.SetResult(new TestCacheEntry<string?> { Value = "v1", Expiration = h.Clock.GetUtcNow().AddHours(1) });
        (await read).Should().Be("v1");
        await h.SettledAsync();

        h.Local("k").Should().Be("v2", "the read began before the set was issued");
    }

    [Fact]
    public async Task A_read_of_the_inner_tier_landing_after_a_remove_puts_nothing_back()
    {
        using var h = new Harness();
        await h.ActivateAsync();
        var reading = new TaskCompletionSource<ICacheEntry<string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        h.OnRead = _ => Interlocked.Increment(ref started) == 1 ? reading.Task : null;
        var read = h.Cache.GetAsync<string>("k", Awaited, Ct).AsTask();
        await h.UntilAsync(() => Volatile.Read(ref started) == 1);

        await h.Cache.RemoveAsync<string>("k", Ct);
        reading.SetResult(new TestCacheEntry<string?> { Value = "v1", Expiration = h.Clock.GetUtcNow().AddHours(1) });
        await read;

        h.Local("k").Should().BeNull("the value the read found was removed after it began");
    }

    [Fact]
    public async Task A_read_landing_while_a_remove_deletes_leaves_nothing_local_once_the_remove_returns()
    {
        using var h = new Harness();
        await h.ActivateAsync();
        var deleting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.OnRemove = _ => deleting.Task;
        var remove = h.Cache.RemoveAsync<string>("k", Ct).AsTask();
        await h.UntilAsync(() => h.Log.Contains("remove:k"));
        var reading = new TaskCompletionSource<ICacheEntry<string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        h.OnRead = _ => Interlocked.Increment(ref started) == 1 ? reading.Task : null;
        var read = h.Cache.GetAsync<string>("k", Awaited, Ct).AsTask();
        await h.UntilAsync(() => Volatile.Read(ref started) == 1);

        reading.SetResult(new TestCacheEntry<string?> { Value = "v1", Expiration = h.Clock.GetUtcNow().AddHours(1) });
        await read;
        deleting.SetResult(true);
        await remove;

        h.OnRead = null;
        h.Local("k").Should().BeNull("the read found the value the remove was deleting");
    }

    [Fact]
    public async Task A_read_begun_after_an_awaited_set_was_issued_does_not_replace_the_value_it_keeps()
    {
        using var h = new Harness();
        await h.ActivateAsync();
        var write = h.Gate("k", "v2");
        var set = h.Cache.SetAsync<string>("k", "v2", Awaited, Ct).AsTask();
        await h.UntilAsync(() => h.Log.Contains("write:k=v2"));
        var reading = new TaskCompletionSource<ICacheEntry<string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        h.OnRead = _ => Interlocked.Increment(ref started) == 1 ? reading.Task : null;
        var read = h.Cache.GetAsync<string>("k", Awaited, Ct).AsTask();
        await h.UntilAsync(() => Volatile.Read(ref started) == 1);

        write.SetResult(true);
        (await set).Should().BeTrue();
        reading.SetResult(new TestCacheEntry<string?> { Value = "v1", Expiration = h.Clock.GetUtcNow().AddHours(1) });
        await read;

        h.Local("k").Should().Be("v2", "the read found the value the set was replacing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_batch_read_keeps_the_keys_no_write_came_after_and_leaves_the_others(bool entries)
    {
        using var h = new Harness();
        // In stripes of their own, so only the set's own key is held back.
        var (b, c, d) = InStripesOfTheirOwn();
        await h.Cache.SetAsync<string>("a", "va", Awaited, Ct);
        // Changed before the read, so its mark differs from the others'.
        await h.Cache.SetAsync<string>(b, "b0", Awaited, Ct);
        await h.Cache.RemoveAsync<string>(b, Ct);
        var reading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CacheKey[]? asked = null;
        h.OnBatchRead = keys =>
        {
            asked = keys;
            return reading.Task.ContinueWith(_ => h.Found(keys, k => k + "1"), TaskScheduler.Default);
        };
        CacheKey[] all = ["a", b, c, d];
        var read = entries
            ? h.Cache.GetCacheEntriesAsync<string>(all, Awaited, Ct).AsTask().ContinueWith(static _ => { }, TaskScheduler.Default)
            : h.Cache.GetAsync<string>(all, Awaited, Ct).AsTask().ContinueWith(static _ => { }, TaskScheduler.Default);
        await h.UntilAsync(() => asked is not null);

        await h.Cache.SetAsync<string>(c, "c2", Background, Ct);
        reading.SetResult(true);
        await read;
        await h.SettledAsync();

        asked!.Select(k => k.Name).Should().Equal(b, c, d);
        h.OnBatchRead = null;
        h.Local("a").Should().Be("va");
        h.Local(b).Should().Be(b + "1");
        h.Local(c).Should().Be("c2", "the set came after the read began");
        h.Local(d).Should().Be(d + "1");
    }

    [Fact]
    public async Task With_background_writes_off_a_batch_read_keeps_what_it_found()
    {
        using var h = new Harness(new InMemoryRedisCacheOptions { MaxBackgroundWrites = 0 });
        h.OnBatchRead = keys => Task.FromResult(h.Found(keys, k => k + "1"));

        await h.Cache.GetAsync<string>(["b", "c"], Awaited, Ct);

        h.OnBatchRead = null;
        h.Local("b").Should().Be("b1");
        h.Local("c").Should().Be("c1");
    }

    [Fact]
    public async Task Without_a_background_write_an_awaited_set_tracks_nothing_and_a_fill_during_it_is_kept()
    {
        using var h = new Harness();
        var write = h.Gate("k", "v2");
        var set = h.Cache.SetAsync<string>("k", "v2", Awaited, Ct).AsTask();
        await h.UntilAsync(() => h.Log.Contains("write:k=v2"));
        h.OnRead = _ => Task.FromResult<ICacheEntry<string?>>(new TestCacheEntry<string?> { Value = "v1", Expiration = h.Clock.GetUtcNow().AddHours(1) });

        (await h.Cache.GetAsync<string>("k", Awaited, Ct)).Should().Be("v1");

        h.Cache.BackgroundWritesActive.Should().BeFalse();
        h.OnRead = null;
        h.Local("k").Should().Be("v1", "as on main, nothing orders reads against awaited writes until a background write is taken");
        write.SetResult(true);
        await set;
    }

    [Fact]
    public async Task Awaited_sets_of_one_key_do_not_wait_for_one_another()
    {
        using var h = new Harness();
        var first = h.Gate("k", "a");
        var slow = h.Cache.SetAsync<string>("k", "a", Awaited, Ct).AsTask();
        await h.UntilAsync(() => h.Log.Contains("write:k=a"));

        (await h.Cache.SetAsync<string>("k", "b", Awaited, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct)).Should().BeTrue();

        first.SetResult(true);
        await slow;
    }

    [Fact]
    public async Task Background_sets_of_other_keys_do_not_wait_for_one_another()
    {
        using var h = new Harness();
        var slow = h.Gate("slow");

        await h.Cache.SetAsync<string>("slow", "a", Background, Ct);
        await h.Cache.SetAsync<string>("fast", "b", Background, Ct);
        await h.UntilAsync(() => h.Log.Contains("event:fast"));

        h.Log.Should().NotContain("event:slow");
        slow.SetResult(true);
        await h.SettledAsync();
    }

    [Fact]
    public async Task An_awaited_set_after_a_background_set_of_the_key_is_written_after_it()
    {
        using var h = new Harness();
        var first = h.Gate("k", "a");
        await h.Cache.SetAsync<string>("k", "a", Background, Ct);

        var second = h.Cache.SetAsync<string>("k", "b", Awaited, Ct).AsTask();

        await Task.Delay(100, Ct);
        second.IsCompleted.Should().BeFalse();
        h.Log.Should().Equal("write:k=a");
        first.SetResult(true);
        (await second).Should().BeTrue();
        h.Log.Should().Equal("write:k=a", "event:k", "write:k=b", "event:k");
    }

    [Fact]
    public async Task A_set_finding_no_place_free_is_written_in_the_foreground_after_the_background_ones_of_its_key()
    {
        using var h = new Harness(new InMemoryRedisCacheOptions { MaxBackgroundWrites = 1 });
        var first = h.Gate("k", "a");
        var other = h.Gate("other");
        await h.Cache.SetAsync<string>("k", "a", Background, Ct);

        var overflow = h.Cache.SetAsync<string>("other", "x", Background, Ct).AsTask();

        await Task.Delay(100, Ct);
        h.Log.Should().Equal("write:k=a", "write:other=x");
        overflow.IsCompleted.Should().BeFalse("with no place free the caller waits for its write");
        other.SetResult(true);
        (await overflow).Should().BeTrue();
        h.Log.Should().Equal("write:k=a", "write:other=x", "event:other");

        var sameKey = h.Cache.SetAsync<string>("k", "b", Background, Ct).AsTask();
        await Task.Delay(100, Ct);
        sameKey.IsCompleted.Should().BeFalse();
        h.Log.Should().NotContain("write:k=b", "it must not overtake the write of the same key still running");
        first.SetResult(true);
        (await sameKey).Should().BeTrue();
        await h.SettledAsync();
    }

    [Fact]
    public async Task Zero_places_turn_background_writes_off()
    {
        using var h = new Harness(new InMemoryRedisCacheOptions { MaxBackgroundWrites = 0 });
        var gate = h.Gate("k");

        var set = h.Cache.SetAsync<string>("k", "v", Background, Ct).AsTask();

        await Task.Delay(100, Ct);
        set.IsCompleted.Should().BeFalse();
        gate.SetResult(true);
        (await set).Should().BeTrue();
    }

    [Fact]
    public async Task The_background_write_does_not_use_the_callers_token()
    {
        using var h = new Harness();
        var gate = h.Gate("k");
        CancellationToken seen = default;
        var inner = h.OnWrite!;
        h.OnWrite = (v, token) =>
        {
            seen = token;
            return inner(v, token);
        };
        using var caller = new CancellationTokenSource();

        await h.Cache.SetAsync<string>("k", "v", Background, caller.Token);
        await caller.CancelAsync();
        gate.SetResult(true);
        await h.SettledAsync();

        seen.Should().NotBe(caller.Token);
        seen.IsCancellationRequested.Should().BeFalse();
        h.Log.Should().Equal("write:k=v", "event:k");
    }

    [Fact]
    public async Task Disposing_the_cache_waits_for_the_background_writes()
    {
        using var h = new Harness();
        var gate = h.Gate("k");
        await h.Cache.SetAsync<string>("k", "v", Background, Ct);

        var dispose = h.Cache.DisposeAsync().AsTask();

        await Task.Delay(100, Ct);
        dispose.IsCompleted.Should().BeFalse();
        gate.SetResult(true);
        await dispose;
        h.Log.Should().Equal("write:k=v", "event:k");
    }

    [Fact]
    public async Task Disposing_the_cache_synchronously_waits_for_the_background_writes()
    {
        using var h = new Harness();
        var gate = h.Gate("k");
        await h.Cache.SetAsync<string>("k", "v", Background, Ct);

        var dispose = Task.Run(h.Cache.Dispose, Ct);

        await Task.Delay(100, Ct);
        dispose.IsCompleted.Should().BeFalse();
        gate.SetResult(true);
        await dispose;
        h.Log.Should().Equal("write:k=v", "event:k");
    }

    [Fact]
    public async Task Disposing_the_cache_gives_up_on_a_stuck_write_after_the_drain_timeout_and_cancels_it()
    {
        using var h = new Harness(new InMemoryRedisCacheOptions { BackgroundWriteDrainTimeout = TimeSpan.FromMilliseconds(200) });
        var stuck = h.Gate("k");
        CancellationToken seen = default;
        var inner = h.OnWrite!;
        h.OnWrite = (v, token) =>
        {
            seen = token;
            return inner(v, token);
        };
        await h.Cache.SetAsync<string>("k", "v", Background, Ct);

        await h.Cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        seen.IsCancellationRequested.Should().BeTrue();
        stuck.SetResult(false);
        await h.SettledAsync();
    }

    [Fact]
    public async Task A_drain_timeout_beyond_what_a_wait_takes_is_bounded_not_thrown()
    {
        using var h = new Harness(new InMemoryRedisCacheOptions { BackgroundWriteDrainTimeout = TimeSpan.MaxValue });
        var stuck = h.Gate("k");
        await h.Cache.SetAsync<string>("k", "v", Background, Ct);

        var dispose = h.Cache.DisposeAsync().AsTask();

        await Task.Delay(200, Ct);
        dispose.IsCompleted.Should().BeFalse("the timeout is bounded, so the drain is still waiting");
        stuck.SetResult(true);
        await dispose.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Disposing_the_cache_asynchronously_waits_the_drain_timeout_once()
    {
        var timeout = TimeSpan.FromSeconds(1);
        using var h = new Harness(new InMemoryRedisCacheOptions { BackgroundWriteDrainTimeout = timeout });
        var stuck = h.Gate("k");
        await h.Cache.SetAsync<string>("k", "v", Background, Ct);
        var elapsed = Stopwatch.StartNew();

        await h.Cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        elapsed.Elapsed.Should().BeLessThan(timeout * 1.6, "the synchronous dispose that follows joins the drain already done");
        stuck.SetResult(false);
        await h.SettledAsync();
    }

    [Fact]
    public async Task An_infinite_drain_timeout_waits_for_every_background_write()
    {
        using var h = new Harness(new InMemoryRedisCacheOptions { BackgroundWriteDrainTimeout = Timeout.InfiniteTimeSpan });
        var stuck = h.Gate("k");
        CancellationToken seen = default;
        var inner = h.OnWrite!;
        h.OnWrite = (v, token) =>
        {
            seen = token;
            return inner(v, token);
        };
        await h.Cache.SetAsync<string>("k", "v", Background, Ct);

        var dispose = h.Cache.DisposeAsync().AsTask();

        await Task.Delay(300, Ct);
        dispose.IsCompleted.Should().BeFalse();
        seen.IsCancellationRequested.Should().BeFalse();
        stuck.SetResult(true);
        await dispose.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        h.Log.Should().Equal("write:k=v", "event:k");
    }

    [Fact]
    public async Task A_drain_that_gives_up_releases_a_background_write_waiting_behind_a_stuck_awaited_one()
    {
        using var h = new Harness(new InMemoryRedisCacheOptions { BackgroundWriteDrainTimeout = TimeSpan.FromMilliseconds(200) });
        await h.ActivateAsync();
        var stuck = h.Gate("k", "w");
        var awaited = h.Cache.SetAsync<string>("k", "w", Awaited, Ct).AsTask();
        await h.UntilAsync(() => h.Log.Contains("write:k=w"));
        await h.Cache.SetAsync<string>("k", "b", Background, Ct);

        await h.Cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await h.SettledAsync();
        h.Log.Should().NotContain("write:k=b");
        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Is<TelemetryTags<string>>(t => Stage(t) == "drain_cancelled"), Arg.Any<TelemetryTags<double>>());
        h.Telemetry.DidNotReceive().TrackEvent("cache.background_write.failed", Arg.Is<TelemetryTags<string>>(t => Stage(t) == "unexpected"), Arg.Any<TelemetryTags<double>>());
        stuck.SetResult(true);
        await awaited;
    }

    [Fact]
    public async Task A_drain_that_gives_up_counts_a_write_it_cancels_inside_the_inner_tier_as_drain_cancelled()
    {
        using var h = new Harness(new InMemoryRedisCacheOptions { BackgroundWriteDrainTimeout = TimeSpan.FromMilliseconds(200) });
        h.OnWrite = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return true;
        };
        await h.Cache.SetAsync<string>("k", "v", Background, Ct);
        await h.UntilAsync(() => h.Log.Contains("write:k=v"));

        await h.Cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await h.SettledAsync();

        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Is<TelemetryTags<string>>(t => Stage(t) == "drain_cancelled"), Arg.Any<TelemetryTags<double>>());
        h.Telemetry.DidNotReceive().TrackEvent("cache.background_write.failed", Arg.Is<TelemetryTags<string>>(t => Stage(t) == "write"), Arg.Any<TelemetryTags<double>>());
    }

    [Fact]
    public async Task A_drain_that_gives_up_counts_an_inner_write_answering_its_cancel_with_false_as_drain_cancelled()
    {
        using var h = new Harness(new InMemoryRedisCacheOptions { BackgroundWriteDrainTimeout = TimeSpan.FromMilliseconds(200) });
        h.OnWrite = (_, token) => RefusedOnCancelAsync(token);
        await h.Cache.SetAsync<string>("k", "v", Background, Ct);
        await h.UntilAsync(() => h.Log.Contains("write:k=v"));

        await h.Cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await h.SettledAsync();

        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Is<TelemetryTags<string>>(t => Stage(t) == "drain_cancelled"), Arg.Any<TelemetryTags<double>>());
        h.Telemetry.DidNotReceive().TrackEvent("cache.background_write.failed", Arg.Is<TelemetryTags<string>>(t => Stage(t) == "write"), Arg.Any<TelemetryTags<double>>());
    }

    [Fact]
    public async Task A_background_set_issued_while_a_try_add_publishes_reaches_the_inner_tier_without_waiting_for_the_event()
    {
        using var h = new Harness();
        var publishing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        h.OnPublish = _ => Interlocked.Increment(ref first) == 1 ? new ValueTask<bool>(publishing.Task) : new ValueTask<bool>(true);
        var add = h.Cache.TryAddAsync<string>("k", "v1", Awaited, Ct).AsTask();
        await h.UntilAsync(() => Volatile.Read(ref first) == 1);

        await h.Cache.SetAsync<string>("k", "v2", Background, Ct);

        await h.UntilAsync(() => h.Log.Contains("write:k=v2"));
        publishing.SetResult(true);
        (await add).Should().BeTrue();
        await h.SettledAsync();
    }


    [Fact]
    public async Task A_write_queued_behind_a_stuck_one_is_given_up_once_the_drain_timeout_cancels()
    {
        using var h = new Harness(new InMemoryRedisCacheOptions { BackgroundWriteDrainTimeout = TimeSpan.FromMilliseconds(200) });
        var stuck = h.Gate("k");
        var tokens = new List<CancellationToken>();
        var inner = h.OnWrite!;
        h.OnWrite = (v, token) =>
        {
            lock (tokens)
            {
                tokens.Add(token);
            }
            return inner(v, token);
        };
        await h.Cache.SetAsync<string>("k", "v", Background, Ct);
        await h.Cache.SetAsync<string>("k", "w", Background, Ct);

        await h.Cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        stuck.SetResult(false);
        await h.SettledAsync();

        lock (tokens)
        {
            tokens.Should().ContainSingle("the queued write is cancelled before it reaches the inner tier");
            tokens[0].IsCancellationRequested.Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_background_set_issued_while_the_cache_drains_is_written_in_the_foreground()
    {
        using var h = new Harness();
        var stuck = h.Gate("k");
        var late = h.Gate("late");
        await h.Cache.SetAsync<string>("k", "v", Background, Ct);
        var dispose = h.Cache.DisposeAsync().AsTask();
        await Task.Delay(100, Ct);

        var set = h.Cache.SetAsync<string>("late", "x", Background, Ct).AsTask();

        await Task.Delay(100, Ct);
        set.IsCompleted.Should().BeFalse("a closing cache takes no more background writes");
        late.SetResult(true);
        (await set).Should().BeTrue();
        stuck.SetResult(true);
        await dispose;
    }

    [Fact]
    public async Task A_fill_returns_with_the_value_local_while_its_write_is_pending_and_publishes_no_event()
    {
        using var h = new Harness();
        var gate = h.Gate("k");

        (await h.Cache.GetOrAddAsync<string>("k", static _ => Task.FromResult<string?>("v"), Background, Ct)).Should().Be("v");

        h.Log.Should().Equal("write:k=v");
        h.Local("k").Should().Be("v");
        gate.SetResult(true);
        await h.SettledAsync();
        h.Log.Should().Equal("write:k=v");
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Local("k").Should().Be("v", "the landed write promoted it past the 30 s cap");
    }

    [Fact]
    public async Task A_fill_without_the_flag_waits_for_its_write()
    {
        using var h = new Harness();
        var gate = h.Gate("k");

        var fill = h.Cache.GetOrAddAsync<string>("k", static _ => Task.FromResult<string?>("v"), Awaited, Ct).AsTask();

        await Task.Delay(100, Ct);
        fill.IsCompleted.Should().BeFalse();
        gate.SetResult(true);
        (await fill).Should().Be("v");
    }

    [Fact]
    public async Task A_fill_the_inner_tier_refuses_stays_local_for_the_cap_only()
    {
        using var h = new Harness();
        h.OnWrite = (_, _) => Task.FromResult(false);

        (await h.Cache.GetOrAddAsync<string>("k", static _ => Task.FromResult<string?>("v"), Background, Ct)).Should().Be("v");
        await h.SettledAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(29));
        h.Local("k").Should().Be("v");
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Local("k").Should().BeNull();
        h.Telemetry.Received(1).TrackEvent("cache.background_write.failed", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());
    }

    [Fact]
    public async Task A_fill_under_the_distributed_lock_is_written_in_the_foreground()
    {
        var locks = Substitute.For<IDistributedLock>();
        locks.AcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IAsyncDisposable>(Substitute.For<IAsyncDisposable>()));
        using var h = new Harness(distributedLock: locks);
        var gate = h.Gate("k");
        var locked = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(10), BackgroundDistributedWrite = true, Lock = new LockProfile { DistributedLockEnabled = true } };

        var fill = h.Cache.GetOrAddAsync<string>("k", static _ => Task.FromResult<string?>("v"), locked, Ct).AsTask();

        await Task.Delay(100, Ct);
        fill.IsCompleted.Should().BeFalse("the lock is released when the run ends, so the value must be in the inner tier by then");
        gate.SetResult(true);
        (await fill).Should().Be("v");
    }

    [Fact]
    public async Task A_batch_set_stays_in_the_foreground_and_waits_for_background_writes_of_its_keys()
    {
        using var h = new Harness();
        var first = h.Gate("k", "a");
        await h.Cache.SetAsync<string>("k", "a", Background, Ct);
        h.OnBatchWrite = () => h.Log.Add("batch");

        var batch = h.Cache.SetAsync<string>([new KeyValuePair<CacheKey, string?>("k", "b")], Background, Ct).AsTask();

        await Task.Delay(100, Ct);
        batch.IsCompleted.Should().BeFalse();
        h.Log.Should().NotContain("batch");
        first.SetResult(true);
        (await batch).Should().BeTrue();
        h.Log.IndexOf("batch").Should().BeGreaterThan(h.Log.IndexOf("event:k"));
    }

    [Fact]
    public async Task The_flag_is_inherited_from_the_default_policy_and_a_named_policy_can_turn_it_off()
    {
        var defaults = new CachePolicy { BackgroundDistributedWrite = true };
        var named = new CachePolicy { BackgroundDistributedWrite = false };

        CachePolicyMerger.Merge(new CachePolicy(), defaults).BackgroundDistributedWrite.Should().BeTrue();
        CachePolicyMerger.Merge(named, defaults).BackgroundDistributedWrite.Should().BeFalse();
        CachePolicyMerger.Merge(new CachePolicy(), new CachePolicy()).BackgroundDistributedWrite.Should().BeNull();
        await Task.CompletedTask;
    }

    private static (string B, string C, string D) InStripesOfTheirOwn()
    {
        var keys = Enumerable.Range(0, 10_000).Select(i => $"k{i}").DistinctBy(BackgroundWrites.Stripe).Take(3).ToArray();
        return (keys[0], keys[1], keys[2]);
    }

    // As the Redis setters do: a cancelled write returns false.
    private static async Task<bool> RefusedOnCancelAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
        }

        return false;
    }

    private static string? Stage(TelemetryTags<string> tags) => tags.FirstOrDefault(t => t.Key == "stage").Value;

    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RecordingTopic(Harness harness) : ITopic<ICacheEvent>
    {
        public TopicKey TopicKey { get; } = new("topic");

        public EventHandler? OnDisposed { get; set; }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer) => NullDisposable.Instance;

        public ValueTask<bool> PublishAsync(ICacheEvent @event, CancellationToken token = default) => harness.Publish(@event);

        public void Dispose()
        {
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<bool>> _gates = [];
        private readonly Dictionary<string, TaskCompletionSource<bool>> _gated = [];

        public Harness(InMemoryRedisCacheOptions? options = null, IDistributedLock? distributedLock = null, bool failingMemory = false, RecordingLogger? logger = null)
        {
            Clock = new StepClock();
            options ??= new InMemoryRedisCacheOptions();
            var inner = Substitute.For<ICache>();
            Telemetry = Substitute.For<ICachingTelemetryProvider>();
            OnWrite = (value, _) =>
            {
                var gate = Gated(value);
                return gate is null ? Task.FromResult(true) : gate.Task;
            };
            ValueTaskStubs.Returns(
                inner.SetAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    var key = call.ArgAt<CacheKey>(0).Name;
                    var value = call.ArgAt<string?>(1);
                    Add($"write:{key}={value}");
                    return new ValueTask<bool>(OnWrite!($"{key}={value}", call.ArgAt<CancellationToken>(4)));
                });
            ValueTaskStubs.Returns(
                inner.SetAsync<string?>(Arg.Any<KeyValuePair<CacheKey, string?>[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                _ =>
                {
                    OnBatchWrite?.Invoke();
                    return new ValueTask<bool>(true);
                });
            ValueTaskStubs.Returns(
                inner.TryAddAsync<string?>(Arg.Any<CacheKey>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    var key = call.ArgAt<CacheKey>(0).Name;
                    bool held;
                    lock (_gate)
                    {
                        held = Log.Exists(e => e.StartsWith($"write:{key}=", StringComparison.Ordinal));
                    }

                    Add($"tryadd:{key}={call.ArgAt<string?>(1)}");
                    return new ValueTask<bool>(!held);
                });
            ValueTaskStubs.Returns(
                inner.RefreshAsync<string>(Arg.Any<CacheKey>(), Arg.Any<DateTimeOffset>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    Add($"refresh:{call.ArgAt<CacheKey>(0).Name}");
                    return new ValueTask<bool>(true);
                });
            ValueTaskStubs.Returns(
                inner.RemoveAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    var key = call.ArgAt<CacheKey>(0).Name;
                    Add($"remove:{key}");
                    return new ValueTask<bool>(OnRemove?.Invoke(key) ?? Task.FromResult(true));
                });
            ValueTaskStubs.Returns(
                inner.GetCacheEntriesAsync<string>(Arg.Any<CacheKey[]>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    var keys = call.ArgAt<CacheKey[]>(0);
                    return OnBatchRead?.Invoke(keys) is { } read
                        ? new ValueTask<KeyValuePair<CacheKey, ICacheEntry<string?>>[]>(read)
                        : new ValueTask<KeyValuePair<CacheKey, ICacheEntry<string?>>[]>(Array.ConvertAll(keys, k => new KeyValuePair<CacheKey, ICacheEntry<string?>>(k, new TestCacheEntry<string?> { Value = null, Expiration = DateTimeOffset.MinValue })));
                });
            ValueTaskStubs.Returns(
                inner.GetCacheEntryAsync<string>(Arg.Any<CacheKey>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()),
                call =>
                {
                    var key = call.ArgAt<CacheKey>(0).Name;
                    return OnRead?.Invoke(key) is { } read
                        ? new ValueTask<ICacheEntry<string?>>(read)
                        : new ValueTask<ICacheEntry<string?>>(new TestCacheEntry<string?> { Value = null, Expiration = DateTimeOffset.MinValue });
                });
            var events = Substitute.For<ICacheEventFactory>();
            events.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
                .Returns(call => new TestCacheEvent { Type = call.ArgAt<string>(1), Data = call.ArgAt<CacheEventData>(2), Source = new Uri("urn:test") });
            var provider = Substitute.For<ITopicProvider>();
            provider.Enabled.Returns(true);
            provider.Create(Arg.Any<TopicKey>()).Returns(new RecordingTopic(this));
            var topics = Substitute.For<ITopicFactory>();
            topics.Get(Arg.Any<string?>()).Returns(provider);
            IMemoryCacheFactory memory = failingMemory ? new ThrowingMemoryCacheFactory() : new MemoryCacheFactory(Clock, NullLoggerFactory.Instance);
            Cache = RunTables.Track(new MultilayerCache(
                KnownCacheProviderNames.InMemoryRedis,
                inner,
                memory,
                NullChangeTokenFactory.Instance,
                topics,
                events,
                Telemetry,
                options,
                options,
                new CacheOptions { AppShortName = "test", ConnectionMonitorEnabled = false },
                distributedLock ?? NullDistributedLock.Instance,
                NullCachePolicyFactory.Instance,
                Clock,
                (ILogger?)logger ?? NullLogger.Instance));
        }

        public StepClock Clock { get; }

        public MultilayerCache Cache { get; }

        public ICachingTelemetryProvider Telemetry { get; }

        public List<string> Log { get; } = [];

        public List<bool> WrittenWhenPublished { get; } = [];

        public Func<string, CancellationToken, Task<bool>>? OnWrite { get; set; }

        public Func<ICacheEvent, ValueTask<bool>>? OnPublish { get; set; }

        public Action? OnBatchWrite { get; set; }

        public Func<string, Task<bool>>? OnRemove { get; set; }

        public Func<string, Task<ICacheEntry<string?>>?>? OnRead { get; set; }

        public Func<CacheKey[], Task<KeyValuePair<CacheKey, ICacheEntry<string?>>[]>?>? OnBatchRead { get; set; }

        public KeyValuePair<CacheKey, ICacheEntry<string?>>[] Found(CacheKey[] keys, Func<string, string> valueOf) =>
            Array.ConvertAll(keys, k => new KeyValuePair<CacheKey, ICacheEntry<string?>>(k, new TestCacheEntry<string?> { Value = valueOf(k.Name), Expiration = Clock.GetUtcNow().AddHours(1) }));

        public string? Local(string key) => Cache.GetAsync<string>(key, Awaited, Ct).AsTask().GetAwaiter().GetResult();

        public TaskCompletionSource<bool> Gate(string key, string? value = null)
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _gates.Add(gate);
                _gated[value is null ? key : $"{key}={value}"] = gate;
                if (value is null)
                {
                    _gated[$"{key}=*"] = gate;
                }
            }

            return gate;
        }

        public void ReleaseAll()
        {
            lock (_gate)
            {
                foreach (var gate in _gates)
                {
                    gate.TrySetResult(true);
                }
            }
        }

        public ValueTask<bool> Publish(ICacheEvent @event)
        {
            lock (_gate)
            {
                WrittenWhenPublished.Add(Log.Count > 0 && Log[^1].StartsWith("write:", StringComparison.Ordinal));
                Log.Add($"event:{@event.Data?.Key}");
            }

            return OnPublish?.Invoke(@event) ?? new ValueTask<bool>(true);
        }

        public async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                (DateTime.UtcNow < deadline).Should().BeTrue("the condition was not met in time: {0}", string.Join(", ", Log));
                await Task.Delay(5, Ct);
            }
        }

        public Task SettledAsync() => UntilAsync(() => Cache.RunsInFlight == 0);

        // Write order is tracked from the first background write on.
        public async Task ActivateAsync()
        {
            await Cache.SetAsync<string>("warm-up", "x", Background, Ct);
            await SettledAsync();
            Log.Clear();
        }

        public void Dispose() => Cache.Dispose();

        private void Add(string entry)
        {
            lock (_gate)
            {
                Log.Add(entry);
            }
        }

        private TaskCompletionSource<bool>? Gated(string keyAndValue)
        {
            lock (_gate)
            {
                if (_gated.TryGetValue(keyAndValue, out var exact))
                {
                    return exact;
                }

                var key = keyAndValue[..keyAndValue.IndexOf('=', StringComparison.Ordinal)];
                return _gated.GetValueOrDefault($"{key}=*") ?? _gated.GetValueOrDefault(key);
            }
        }
    }

    private sealed class ThrowingMemoryCacheFactory : IMemoryCacheFactory
    {
        public IMemoryCache Get(IMemoryCacheOptions memoryOptions) => new ThrowingMemoryCache();
    }

    private sealed class ThrowingMemoryCache : IMemoryCache
    {
        public Microsoft.Extensions.Caching.Memory.ICacheEntry CreateEntry(object key) => throw new InvalidOperationException("the local tier is broken");

        public bool TryGetValue(object key, out object? value)
        {
            value = null;
            return false;
        }

        public void Remove(object key)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> _records = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Records => [.. _records];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _records.Enqueue((logLevel, formatter(state, exception)));
    }
}
