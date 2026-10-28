using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

#pragma warning disable xUnit1051 // the shapes under test pass `default` for the token or leave it out

/// <summary>Calls in the shapes callers write compile and reach the closure member, not the state or argument overload beside it.</summary>
public class OverloadShapeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Closure_shapes_on_the_hash_caches_bind_to_the_closure_members()
    {
        var hash = Substitute.For<IHashCache>();
        var typed = Substitute.For<IHashCache<string>>();
        var generator = new Func<CancellationToken, Task<IDictionary<string, string?>>>(_ => throw new InvalidOperationException());

        await hash.GetOrAddAsync<string>("k", _ => throw new InvalidOperationException(), null, Ct);
        await hash.GetOrAddAsync<string>("k", async _ => default!, TimeSpan.FromMinutes(1), null, Ct);
        await hash.GetOrAddAsync("k", generator, null, Ct);
        await hash.GetOrAddAsync("k", generator, DateTimeOffset.MaxValue, HashCacheSetOption.KeyReplace, null);
        await typed.GetOrAddAsync("k", _ => throw new InvalidOperationException(), Ct);
        await typed.GetOrAddAsync("k", async _ => default!, TimeSpan.FromMinutes(1), Ct);
        await typed.GetOrAddAsync("k", generator, default);
        await typed.GetOrAddAsync((CacheKey)default, generator, Ct);

        await hash.Received(2).GetOrAddAsync<string>(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), null, Ct);
        await hash.Received(1).GetOrAddAsync<string>(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), TimeSpan.FromMinutes(1), null, Ct);
        await typed.Received(1).GetOrAddAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), TimeSpan.FromMinutes(1), Ct);
        await typed.Received(3).GetOrAddAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Closure_shapes_of_GetOrAddWithExpirationAsync_bind_to_the_closure_members()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        ValueTaskStubs.Returns(cache.GetOrAddWithExpirationAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<GeneratedValue<string>>>>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()), "hit");
        ValueTaskStubs.Returns(typed.GetOrAddWithExpirationAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<GeneratedValue<string>>>>(), Arg.Any<CancellationToken>()), "hit");
        var generator = new Func<CancellationToken, Task<GeneratedValue<string>>>(_ => throw new InvalidOperationException());

        (await cache.GetOrAddWithExpirationAsync<string>("k", _ => throw new InvalidOperationException(), null, Ct)).Should().Be("hit");
        (await cache.GetOrAddWithExpirationAsync<string>("k", async _ => default, Ct)).Should().Be("hit");
        (await cache.GetOrAddWithExpirationAsync("k", generator, null)).Should().Be("hit");
        (await typed.GetOrAddWithExpirationAsync("k", _ => throw new InvalidOperationException(), Ct)).Should().Be("hit");
        (await typed.GetOrAddWithExpirationAsync("k", async _ => default, Ct)).Should().Be("hit");
        (await typed.GetOrAddWithExpirationAsync("k", generator, default)).Should().Be("hit");
    }

    [Fact]
    public async Task Batch_closure_shapes_bind_to_the_batch_members_not_the_argument_ones()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        var entries = new[] { new KeyValuePair<CacheKey, int>("k", 1) };
        var generator = new Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>((_, _) => throw new InvalidOperationException());

        await cache.GetOrAddAsync<string, int>(entries, (_, _) => throw new InvalidOperationException(), null, Ct);
        await cache.GetOrAddAsync(entries, generator, null, Ct);
        await cache.GetOrAddAsync(entries, generator, TimeSpan.FromMinutes(1), null);
        await typed.GetOrAddAsync(entries, (_, _) => throw new InvalidOperationException(), Ct);
        await typed.GetOrAddAsync(entries, async (_, _) => default!, TimeSpan.FromMinutes(1), Ct);
        await typed.GetOrAddAsync(entries, generator, default);

        await cache.Received(2).GetOrAddAsync(entries, Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), null, Ct);
        await cache.Received(1).GetOrAddAsync(entries, generator, TimeSpan.FromMinutes(1), null, default);
        await typed.Received(2).GetOrAddAsync(entries, Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), Arg.Any<CancellationToken>());
        await typed.Received(1).GetOrAddAsync(entries, Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), TimeSpan.FromMinutes(1), Ct);
    }
}
