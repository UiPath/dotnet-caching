using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>A hash fill writes only an absent key: a hash a peer wrote while the generator ran is the answer and is not replaced.</summary>
public class SafeHashFillTests
{
    private static readonly ManualTimeProvider Clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly CachePolicy Awaited = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };

    private static readonly CachePolicy Background = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30), BackgroundDistributedWrite = true };

    private static readonly HashCacheEntryOptions Merge = new(SetOption: HashCacheSetOption.HashReplace);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HashCacheSetOption.KeyReplace)]
    [InlineData(HashCacheSetOption.HashReplace)]
    public async Task A_fill_never_replaces_a_hash_a_peer_wrote_while_its_generator_ran(HashCacheSetOption option)
    {
        // The peer's event is not heard, so the write is found by the fill itself, which is the NX of the key.
        using var c = new PeerCluster(Clock) { AutoDeliver = false };
        var (writer, other) = (c.Add("a"), c.Add("b"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IDictionary<string, string?>> Generate(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return Fields(("a", "1"));
        }

        var call = writer.Cache.GetOrAddAsync<string>("k", Generate, Clock.GetUtcNow().AddMinutes(5), option, Awaited, Ct).AsTask();
        await started.Task.WaitAsync(Ct);

        (await other.Cache.SetAsync<string>("k", Fields(("z", "0")), Merge, Awaited, Ct)).Should().BeTrue();
        release.SetResult();

        (await call).Should().BeEquivalentTo(Fields(("z", "0")), "the hash the peer wrote is the answer");
        c.Redis.Fields().Should().BeEquivalentTo(Fields(("z", "0")), "the fill did not replace it");
        var reads = writer.InnerReads;
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("z", "0")));
        writer.InnerReads.Should().Be(reads, "the stored hash is kept locally");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hash_fill_whose_key_was_changed_while_its_generator_ran_writes_and_keeps_nothing(bool removed)
    {
        using var c = new PeerCluster(Clock);
        var (writer, other) = (c.Add("a"), c.Add("b"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IDictionary<string, string?>> Generate(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return Fields(("a", "1"));
        }

        var call = writer.Cache.GetOrAddAsync<string>("k", Generate, Clock.GetUtcNow().AddMinutes(5), HashCacheSetOption.KeyReplace, Awaited, Ct).AsTask();
        await started.Task.WaitAsync(Ct);

        if (removed)
        {
            await other.Cache.RemoveAsync<string>("k", Ct);
        }
        else
        {
            (await other.Cache.SetAsync<string>("k", Fields(("z", "0")), Merge, Awaited, Ct)).Should().BeTrue();
        }

        release.SetResult();

        (await call).Should().BeEquivalentTo(Fields(("a", "1")), "the caller still gets what its generator made");
        var read = await writer.Cache.GetAsync<string>("k", Awaited, Ct);
        if (removed)
        {
            c.Redis.Fields().Should().BeNull("what the generator made may be older than the removal, so it is not stored");
            read.Should().BeNullOrEmpty("nothing was kept locally either");
        }
        else
        {
            c.Redis.Fields().Should().BeEquivalentTo(Fields(("z", "0")), "what the generator made may be older than the write, so it does not meet it");
            read.Should().BeEquivalentTo(Fields(("z", "0")), "nothing was kept locally either");
        }
    }

    [Fact]
    public async Task A_fill_of_an_absent_hash_writes_it_and_keeps_it()
    {
        using var c = new PeerCluster(Clock);
        var writer = c.Add("a");

        (await writer.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(Fields(("a", "1"))), Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1")));

        c.Redis.Fields().Should().BeEquivalentTo(Fields(("a", "1")));
        var reads = writer.InnerReads;
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1")));
        writer.InnerReads.Should().Be(reads);
    }

    [Fact]
    public async Task A_background_fill_that_finds_the_hash_stored_drops_the_copy_it_kept_ahead_of_the_write()
    {
        using var c = new PeerCluster(Clock) { AutoDeliver = false };
        var (writer, other) = (c.Add("a"), c.Add("b"));
        var release = new TaskCompletionSource();
        writer.Inner.Hold = release.Task;
        await writer.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(Fields(("a", "1"))), Background, Ct);
        (await other.Cache.SetAsync<string>("k", Fields(("z", "0")), Merge, Awaited, Ct)).Should().BeTrue();

        release.SetResult();
        await writer.Settled();

        c.Redis.Fields().Should().BeEquivalentTo(Fields(("z", "0")), "the fill did not replace the peer's hash");
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("z", "0")), "the copy ahead of the write was dropped, so the read took the stored hash");
    }

    [Fact]
    public async Task A_background_fill_waiting_in_the_queue_sends_no_command_when_a_peer_removed_the_key_meanwhile()
    {
        using var c = new PeerCluster(Clock);
        var (writer, other) = (c.Add("a"), c.Add("b"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Inner.Hold = release.Task;

        // The first write is held before the store, so the fill that follows waits behind it.
        (await writer.Cache.SetAsync<string>("k", Fields(("s", "1")), Background, Ct)).Should().BeTrue();
        writer.Local.Remove("k");
        await writer.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(Fields(("a", "1"))), Background, Ct);
        writer.Cache.FillsWatched.Should().BeTrue("the queued fill keeps its watch past its generator");
        await other.Cache.RemoveAsync<string>("k", Ct);

        release.SetResult();
        await writer.Settled();

        writer.Inner.CountedWrites.Should().Be(1, "only the first write reached the store: the key was removed after the generator and before the queued command");
        c.Redis.Fields().Should().BeEquivalentTo(Fields(("s", "1")));
        writer.Cache.FillsWatched.Should().BeFalse("the watch ends with the queued write");
    }

    [Fact]
    public async Task A_hash_fill_is_not_kept_locally_when_the_key_is_removed_after_its_write()
    {
        using var c = new PeerCluster(Clock);
        var (writer, other) = (c.Add("a"), c.Add("b"));
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Inner.AfterWrite = _ => { written.TrySetResult(); return landed.Task; };
        var fill = writer.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(Fields(("a", "1"))), Awaited, Ct).AsTask();
        await written.Task.WaitAsync(Ct);

        await other.Cache.RemoveAsync<string>("k", Ct);
        landed.SetResult();

        (await fill).Should().BeEquivalentTo(Fields(("a", "1")), "the caller still gets what its generator made");
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeNullOrEmpty("the keep was refused, so the read goes to the store");
    }

    [Fact]
    public async Task A_hash_fill_is_not_kept_locally_when_the_key_changes_while_the_keep_waits_for_the_key_turn()
    {
        using var c = new PeerCluster(Clock);
        var writer = c.Add("a");
        var background = (BackgroundWrites)typeof(MultilayerCacheBase).GetField("_background", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(writer.Cache)!;
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Inner.AfterWrite = _ => { written.TrySetResult(); return landed.Task; };
        var fill = writer.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(Fields(("a", "1"))), Awaited, Ct).AsTask();
        await written.Task.WaitAsync(Ct);

        // Holds the key's turn after the write, so the keep that follows waits for it.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var free = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Hold(int _)
        {
            held.SetResult();
            free.Task.Wait(Ct);
            return true;
        }

        var holder = Task.Run(() => background.Apply("k", 0, Hold), Ct);
        await held.Task.WaitAsync(Ct);
        landed.SetResult();
        fill.IsCompleted.Should().BeFalse("the keep waits for the key's turn");

        background.NoteChange("k");
        free.SetResult();
        await holder;
        await fill;

        var reads = writer.InnerReads;
        await writer.Cache.GetAsync<string>("k", Awaited, Ct);
        writer.InnerReads.Should().Be(reads + 1, "the change that came while the keep waited refused the keep, so the read goes to the store");
    }

    [Fact]
    public async Task A_fill_over_an_unreadable_hash_replaces_it()
    {
        using var c = new PeerCluster(Clock);
        var writer = c.Add("a");
        c.Redis.WriteUnreadableQuietly("k");

        (await writer.Cache.GetOrAddAsync<string>("k", _ => Task.FromResult<IDictionary<string, string?>>(Fields(("a", "1"))), Awaited, Ct)).Should().BeEquivalentTo(Fields(("a", "1")));

        c.Redis.Fields().Should().BeEquivalentTo(Fields(("a", "1")), "a fill that left the unreadable hash would never heal it");
    }

    [Fact]
    public async Task A_fill_that_found_an_unreadable_hash_leaves_what_a_peer_wrote_since()
    {
        using var c = new PeerCluster(Clock);
        var writer = c.Add("a");
        c.Redis.WriteUnreadableQuietly("k");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IDictionary<string, string?>> Generate(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return Fields(("a", "1"));
        }

        var call = writer.Cache.GetOrAddAsync<string>("k", Generate, Awaited, Ct).AsTask();
        await started.Task.WaitAsync(Ct);
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Inner.ReadHold = landed.Task;
        writer.Inner.HeldReadSnapshots = true;
        var reads = writer.InnerReads;
        release.SetResult();

        await writer.Inner.Changes.WaitUntil(() => writer.InnerReads > reads);
        call.IsCompleted.Should().BeFalse("the fill reads the unreadable hash before it replaces it");
        c.Redis.Write("k", Fields(("z", "0")), new HashCacheEntryOptions(SetOption: HashCacheSetOption.KeyReplace), default, counted: false);
        landed.SetResult();
        await call;

        c.Redis.Fields().Should().BeEquivalentTo(Fields(("z", "0")), "an unreadable hash is replaced only while it is still what was read");
    }
    private static Dictionary<string, string?> Fields(params (string Field, string Value)[] fields) =>
        fields.ToDictionary(f => f.Field, f => (string?)f.Value);
}
