using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>A hash fill writes only an absent key: a hash a peer wrote while the generator ran is the answer and is not replaced.</summary>
public class SafeHashFillTests
{
    private static readonly CachePolicy Awaited = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30) };

    private static readonly CachePolicy Background = new() { LocalExpiration = TimeSpan.FromMinutes(10), LocalExpirationDisconnected = TimeSpan.FromSeconds(30), BackgroundDistributedWrite = true };

    private static readonly HashCacheEntryOptions Merge = new(SetOption: HashCacheSetOption.HashReplace);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HashCacheSetOption.KeyReplace)]
    [InlineData(HashCacheSetOption.HashReplace)]
    public async Task A_fill_never_replaces_a_hash_a_peer_wrote_while_its_generator_ran(HashCacheSetOption option)
    {
        using var c = new PeerCluster();
        var (writer, other) = (c.Add("a"), c.Add("b"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IDictionary<string, string?>> Generate(CancellationToken _)
        {
            started.TrySetResult();
            await release.Task;
            return Fields(("a", "1"));
        }

        var call = writer.Cache.GetOrAddAsync<string>("k", Generate, DateTimeOffset.UtcNow.AddMinutes(5), option, Awaited, Ct).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        (await other.Cache.SetAsync<string>("k", Fields(("z", "0")), Merge, Awaited, Ct)).Should().BeTrue();
        release.SetResult();

        (await call).Should().BeEquivalentTo(Fields(("z", "0")), "the hash the peer wrote is the answer");
        c.Redis.Fields().Should().BeEquivalentTo(Fields(("z", "0")), "the fill did not replace it");
        var reads = writer.InnerReads;
        (await writer.Cache.GetAsync<string>("k", Awaited, Ct)).Should().BeEquivalentTo(Fields(("z", "0")));
        writer.InnerReads.Should().Be(reads, "the stored hash is kept locally");
    }

    [Fact]
    public async Task A_fill_of_an_absent_hash_writes_it_and_keeps_it()
    {
        using var c = new PeerCluster();
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
        using var c = new PeerCluster { AutoDeliver = false };
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

    private static Dictionary<string, string?> Fields(params (string Field, string Value)[] fields) =>
        fields.ToDictionary(f => f.Field, f => (string?)f.Value);
}
