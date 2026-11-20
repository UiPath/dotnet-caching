using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

/// <summary>A local-only snapshot reports when the local tier evicts the entry, which the local cap can make earlier than the expiration asked for.</summary>
public class LocalSnapshotDeadlineTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    public enum Write
    {
        Set,
        BatchSet,
        GetOrAdd,
        Fill,
        ChangeFills,
        ChangeReplaces,
        MetadataRefresh,
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Write.Set, 1)]
    [InlineData(Write.Set, 120)]
    [InlineData(Write.BatchSet, 1)]
    [InlineData(Write.BatchSet, 120)]
    [InlineData(Write.GetOrAdd, 1)]
    [InlineData(Write.GetOrAdd, 120)]
    [InlineData(Write.Fill, 1)]
    [InlineData(Write.Fill, 120)]
    [InlineData(Write.ChangeFills, 1)]
    [InlineData(Write.ChangeFills, 120)]
    [InlineData(Write.ChangeReplaces, 1)]
    [InlineData(Write.ChangeReplaces, 120)]
    [InlineData(Write.MetadataRefresh, 1)]
    [InlineData(Write.MetadataRefresh, 120)]
    public async Task The_snapshot_reports_the_deadline_the_local_tier_evicts_at(Write write, int capMinutes)
    {
        var cap = TimeSpan.FromMinutes(capMinutes);
        var clock = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var refresh = new TestChangeToken { ActiveChangeCallbacks = true, MetadataHasChanged = true, Expiration = clock.GetUtcNow() + TimeSpan.FromMinutes(90) };
        var tokens = new Queue<ICacheChangeToken>([refresh]);
        using var sut = InMemoryMultilayer.Cache(
            new InMemoryCacheOptions { LocalMaxExpiration = cap },
            clock: clock,
            tokens: write == Write.MetadataRefresh
                ? new TestChangeTokenFactory((_, _) => tokens.Count > 0 ? tokens.Dequeue() : new TestChangeToken { ActiveChangeCallbacks = true })
                : null);

        var requested = clock.GetUtcNow() + Ttl;
        switch (write)
        {
            case Write.Set:
                await sut.SetAsync("k", "v", Ttl, Ct);
                break;
            case Write.BatchSet:
                await sut.SetAsync<string>([new("k", "v")], Ttl, null, Ct);
                break;
            case Write.GetOrAdd:
                await sut.GetOrAddAsync("k", _ => Task.FromResult<string?>("v"), Ttl, Ct);
                break;
            case Write.Fill:
                await sut.TryFillAsync("k", "v", Ttl, Ct);
                break;
            case Write.ChangeFills:
                await sut.SetIfChangedAsync("k", "v", StringComparer.Ordinal, Ttl, Ct);
                break;
            case Write.ChangeReplaces:
                await sut.SetIfChangedAsync("k", "u", StringComparer.Ordinal, Ttl, Ct);
                await sut.SetIfChangedAsync("k", "v", StringComparer.Ordinal, Ttl, Ct);
                break;
            case Write.MetadataRefresh:
                await sut.SetAsync("k", "v", Ttl, Ct);
                clock.Advance(TimeSpan.FromSeconds(10));
                requested = refresh.Expiration!.Value;
                refresh.InvokeCallbacks();
                break;
        }

        // A deadline computed at read time instead of write time would move with the clock.
        var capped = clock.GetUtcNow() + cap;
        clock.Advance(TimeSpan.FromSeconds(30));
        var entry = await sut.AsMigrationCache().ReadSnapshotAsync<string>("k", null, Ct);

        entry.Value.Should().Be("v");
        entry.Expiration.Should().Be(requested < capped ? requested : capped);
    }
}
