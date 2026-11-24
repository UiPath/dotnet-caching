using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Redis;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveSweepTests(RedisContainerFixture fixture)
{
    private const string Group = "grp";

    private static readonly DefaultRedisKeyStrategyFactory Strategies = new();

    private readonly string _app = "sw" + Guid.NewGuid().ToString("N")[..10];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "cp:")]
    public async Task The_sweep_unlinks_every_string_and_hash_key_of_the_cache_and_leaves_every_other_key(bool shardKeys, string connectorPrefix)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true, prefix: connectorPrefix);
        var library = _app + ":";
        var sibling = Cache("sibling", "ordx", shardKeys, library);
        var nested = Cache("nested", "ord:eu", shardKeys, library, owns: k => k.StartsWith($"{library}ord:eu:", StringComparison.Ordinal));
        var ordersOnly = Cache("orders", "ord", shardKeys, library, owns: k => k.StartsWith($"{library}ord:", StringComparison.Ordinal) && !k.StartsWith($"{library}ord:eu:", StringComparison.Ordinal));
        var space = new RegisteredKeySpace([ordersOnly, sibling, nested], library);
        var mine = new List<string>();
        var others = new List<string>();
        var hash = Native("ord", shardKeys, "a-hash");
        foreach (var n in Enumerable.Range(0, 40))
        {
            var key = Native("ord", shardKeys, "k" + n);
            await kit.Db.StringSetAsync(connectorPrefix + key, "v");
            mine.Add(key);
        }

        await kit.Db.HashSetAsync(connectorPrefix + hash, [new HashEntry("f1", "1"), new HashEntry("f2", "2")]);
        mine.Add(hash);
        foreach (var other in new[] { Native("ordx", shardKeys, "k1"), Native("ord:eu", shardKeys, "k1"), Native("orders2", shardKeys, "k1") })
        {
            await kit.Db.StringSetAsync(connectorPrefix + other, "v");
            others.Add(other);
        }

        var bare = Native("ord", shardKeys, "bare");
        if (connectorPrefix.Length > 0)
        {
            await kit.Db.StringSetAsync(bare, "v");
            others.Add(bare);
        }

        var sweep = SweepOver(kit, space, connectorPrefix, batch: 7);
        try
        {
            (await sweep.SweepAsync(Group, "orders", Ct)).Should().Be(mine.Count);

            foreach (var key in mine)
            {
                (await kit.Db.KeyExistsAsync(connectorPrefix + key)).Should().BeFalse(key);
            }

            foreach (var key in others)
            {
                (await kit.Db.KeyExistsAsync(key.Equals(bare, StringComparison.Ordinal) ? key : connectorPrefix + key)).Should().BeTrue($"{key} is not this cache's");
            }
        }
        finally
        {
            await kit.Db.KeyDeleteAsync([.. others.Select(k => (RedisKey)(k == bare ? k : connectorPrefix + k))]);
        }

    }

    [Fact]
    public async Task The_sweep_unlinks_the_fence_and_the_staging_keys_of_the_cache_only()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var library = _app + ":";
        var space = new RegisteredKeySpace([Cache("orders", "ord", false, library), Cache("sibling", "ordx", false, library)], library);
        var mine = Native("ord", false, "k1");
        var other = Native("ordx", false, "k1");
        var made = new List<RedisKey>
        {
            mine,
            kit.Names.ForwardFence(mine),
            kit.Names.ForwardStaging(mine, MoveScripts.NewNonce()),
        };
        var theirs = new List<RedisKey>
        {
            other,
            kit.Names.ForwardFence(other),
            kit.Names.ForwardStaging(other, MoveScripts.NewNonce()),

            // The move's own control and state keys and the reverse names carry no per-cache pattern and are never swept.
            kit.Names.State(kit.Group),
            kit.Names.ReverseFence("in:place:k1"),
        };
        foreach (var key in made.Concat(theirs))
        {
            await kit.Db.StringSetAsync(key, "v");
        }

        try
        {
            (await SweepOver(kit, space, string.Empty).SweepAsync(Group, "orders", Ct)).Should().Be(3);

            foreach (var key in made)
            {
                (await kit.Db.KeyExistsAsync(key)).Should().BeFalse(key.ToString());
            }

            foreach (var key in theirs)
            {
                (await kit.Db.KeyExistsAsync(key)).Should().BeTrue(key.ToString());
            }
        }
        finally
        {
            await kit.Db.KeyDeleteAsync([.. theirs, .. made]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_direct_cache_next_to_the_cleared_one_keeps_its_keys_even_under_a_greedy_registration(bool shardKeys)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var library = _app + ":";
        var greedy = new KeyMoveCache("orders", Group, false, false, false, null, library + "*", k => k, k => k, library + "*", k => library + "n:" + k, k => k, _ => true, _ => true);
        var excluded = new[] { $"{_app}:{RedisKeyspaces.String}:*", $"{_app}:{RedisKeyspaces.Hash}:*" };
        var space = new RegisteredKeySpace([greedy], library, null, excluded);
        var direct = new[]
        {
            Format(Strategies.Create(Options(shardKeys), RedisKeyspaces.String), "plain"),
            Format(Strategies.Create(Options(shardKeys), RedisKeyspaces.Hash), "a-hash"),
        };
        var movingKey = library + "n:k1";
        foreach (var key in direct.Append(movingKey))
        {
            await kit.Db.StringSetAsync(key, "v");
        }

        try
        {
            (await SweepOver(kit, space, string.Empty).SweepAsync(Group, "orders", Ct)).Should().Be(1);

            (await kit.Db.KeyExistsAsync(movingKey)).Should().BeFalse();
            foreach (var key in direct)
            {
                (await kit.Db.KeyExistsAsync(key)).Should().BeTrue($"the direct cache's {key} is not the move's");
            }
        }
        finally
        {
            await kit.Db.KeyDeleteAsync([.. direct.Select(k => (RedisKey)k), movingKey]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_pattern_of_a_cache_matches_each_of_its_keys_and_no_key_of_a_cache_whose_name_shares_a_prefix(bool shardKeys)
    {
        var library = _app + ":";
        var names = new[] { "ord", "ordx", "ord-eu", "or", "o*d", "o?d", "o[a]d", "o\\d", "ord*", "ord[", "ordx[1]" };
        var caches = names.Select(n => Cache(n, n, shardKeys, library)).ToArray();
        var direct = new[]
        {
            Format(Strategies.Create(Options(shardKeys), RedisKeyspaces.String), "ord:k"),
            Format(Strategies.Create(Options(shardKeys), RedisKeyspaces.Hash), "ord:k"),
        };
        var keyNames = new[] { "k", "k1", "a:b", "ord", "x*", "[1]", "\\", "{t}k" };

        foreach (var cache in caches)
        {
            foreach (var name in keyNames)
            {
                var own = Native(cache.Name, shardKeys, name);
                GlobPatterns.Matches(cache.NativePattern, own).Should().BeTrue($"{own} is a key of {cache.Name}, pattern {cache.NativePattern}");
                cache.Owns(MoveSide.Native, own).Should().BeTrue();
                foreach (var other in caches.Where(c => c != cache))
                {
                    var foreign = Native(other.Name, shardKeys, name);
                    GlobPatterns.Matches(cache.NativePattern, foreign).Should().BeFalse($"{foreign} is a key of {other.Name}, not of {cache.Name} (pattern {cache.NativePattern})");
                    cache.Owns(MoveSide.Native, foreign).Should().BeFalse();
                }

                foreach (var key in direct)
                {
                    GlobPatterns.Matches(cache.NativePattern, key).Should().BeFalse($"the direct key {key}");
                }
            }
        }
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_sweep_after_the_batch_it_is_in()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var library = _app + ":";
        var cache = Cache("orders", "ord", false, library);
        using var stop = new CancellationTokenSource();
        var asked = 0;
        var space = new CancellingSpace(new RegisteredKeySpace([cache], library), () =>
        {
            if (++asked == 5)
            {
                stop.Cancel();
            }
        });
        var keys = Enumerable.Range(0, 30).Select(n => Native("ord", false, "k" + n)).ToArray();
        await kit.Db.StringSetAsync([.. keys.Select(k => new KeyValuePair<RedisKey, RedisValue>(k, "v"))]);

        try
        {
            var act = () => SweepOver(kit, space, string.Empty, batch: 1).SweepAsync(Group, "orders", stop.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            var left = 0;
            foreach (var key in keys)
            {
                left += await kit.Db.KeyExistsAsync(key) ? 1 : 0;
            }

            left.Should().BeGreaterThan(20, "the scan stopped near the fifth key instead of going on to the thirtieth");
        }
        finally
        {
            await kit.Db.KeyDeleteAsync([.. keys.Select(k => (RedisKey)k)]);
        }
    }

    [Fact]
    public async Task A_cache_with_no_pattern_in_the_group_is_refused_instead_of_swept_for_nothing()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var library = _app + ":";
        var space = new RegisteredKeySpace([Cache("orders", "ord", false, library)], library);

        var act = () => SweepOver(kit, space, string.Empty).SweepAsync(Group, "unknown", Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_key_that_two_caches_own_is_not_unlinked_and_the_sweep_fails()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var library = _app + ":";
        var first = Cache("orders", "ord", false, library);
        var second = Cache("again", "ord", false, library);
        var space = new RegisteredKeySpace([first, second], library);
        var key = Native("ord", false, "k1");
        await kit.Db.StringSetAsync(key, "v");

        try
        {
            var act = () => SweepOver(kit, space, string.Empty).SweepAsync(Group, "orders", Ct);

            await act.Should().ThrowAsync<AmbiguousKeyException>();
            (await kit.Db.KeyExistsAsync(key)).Should().BeTrue();
        }
        finally
        {
            await kit.Db.KeyDeleteAsync(key);
        }
    }

    [Fact]
    public async Task The_sweep_unlinks_across_every_slot_of_a_cluster()
    {
        await using var kit = await StateKit.ClusterAsync(true);
        var library = _app + ":";
        var cache = new KeyMoveCache("orders", Group, false, false, false, null, library + "*", k => k, k => k, library + "n:*", k => library + "n:" + k, k => k[(library.Length + 2)..], null, null);
        var space = new RegisteredKeySpace([cache], library);
        var keys = new List<string>();
        foreach (var n in Enumerable.Range(0, 80))
        {
            var key = $"{library}n:{{t{n}}}";
            await kit.Db.StringSetAsync(key, "v");
            await kit.Db.StringSetAsync(kit.Names.ForwardFence(key), "f");
            keys.Add(key);
        }

        var keep = $"{library}m:{{keep}}";
        await kit.Db.StringSetAsync(keep, "v");
        try
        {
            (await SweepOver(kit, space, string.Empty, batch: 16).SweepAsync(Group, "orders", Ct)).Should().Be(160);

            foreach (var key in keys)
            {
                (await kit.Db.KeyExistsAsync(key)).Should().BeFalse(key);
                (await kit.Db.KeyExistsAsync(kit.Names.ForwardFence(key))).Should().BeFalse(key);
            }

            (await kit.Db.KeyExistsAsync(keep)).Should().BeTrue();
        }
        finally
        {
            await kit.Db.KeyDeleteAsync(keep);
        }
    }

    [Fact]
    public async Task A_pattern_that_ends_in_literal_text_still_gets_its_staging_keys_swept_and_a_neighbour_s_are_left()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var library = _app + ":";
        KeyMoveCache Literal(string name, string stem) => new(name, Group, false, false, false, null, library + "l:*", k => k, k => k, $"{library}{stem}:*:value", k => $"{library}{stem}:{k}:value", k => k.StartsWith($"{library}{stem}:", StringComparison.Ordinal) && k.EndsWith(":value", StringComparison.Ordinal) ? k : null);
        var space = new RegisteredKeySpace([Literal("orders", "n"), Literal("other", "m")], library);
        var data = $"{library}n:k1:value";
        var neighbour = $"{library}m:k1:value";
        var made = new RedisKey[] { data, kit.Names.ForwardFence(data), kit.Names.ForwardStaging(data, MoveScripts.NewNonce()), kit.Names.ForwardStaging(data, MoveScripts.NewNonce()) };
        var theirs = new RedisKey[] { neighbour, kit.Names.ForwardFence(neighbour), kit.Names.ForwardStaging(neighbour, MoveScripts.NewNonce()) };
        foreach (var key in made.Concat(theirs))
        {
            await kit.Db.StringSetAsync(key, "v");
        }

        try
        {
            (await SweepOver(kit, space, string.Empty).SweepAsync(Group, "orders", Ct)).Should().Be(4);

            foreach (var key in made)
            {
                (await kit.Db.KeyExistsAsync(key)).Should().BeFalse(key.ToString());
            }

            foreach (var key in theirs)
            {
                (await kit.Db.KeyExistsAsync(key)).Should().BeTrue(key.ToString());
            }
        }
        finally
        {
            await kit.Db.KeyDeleteAsync([.. theirs, .. made]);
        }
    }

    private static KeyMoveSweep SweepOver(StateKit kit, IGroupKeySpace space, string connectorPrefix, int batch = 512) =>
        new(new MasterScanner(() => kit.Mux.GetServers(), kit.Topology, 0, connectorPrefix, TimeProvider.System, TimeSpan.Zero), space, kit.ControlDb, kit.Names, batch);

    private static string Format(IRedisKeyStrategy strategy, string name) => strategy.GetRedisKey(new CacheKey(name)).ToString();

    private CacheOptions Options(bool shardKeys)
    {
        var options = new CacheOptions { AppShortName = _app };

        // The setting is obsolete but still honored.
        typeof(CacheOptions).GetProperty("ShardKeyEnabled")!.SetValue(options, shardKeys);
        return options;
    }

    private string Native(string differentiator, bool shardKeys, string name)
    {
        return Format(Strategies.Create(Options(shardKeys), differentiator), name);
    }

    // The cache's N keys are the ones the library's key strategy makes under its differentiator; the pattern is that prefix, escaped, then a wildcard.
    private KeyMoveCache Cache(string name, string differentiator, bool shardKeys, string library, Func<string, bool>? owns = null)
    {
        var prefix = $"{_app}:{differentiator}:";
        return new KeyMoveCache(
            name,
            Group,
            false,
            false,
            false,
            null,
            library + "l:" + name + ":*",
            k => library + "l:" + name + ":" + k,
            k => k[(library.Length + 2 + name.Length + 1)..],
            MasterScanner.EscapeGlob(prefix) + "*",
            k => Native(differentiator, shardKeys, k),
            k => k.StartsWith(prefix, StringComparison.Ordinal) ? k[prefix.Length..] : null,
            null,
            owns);
    }

    private sealed class CancellingSpace(IGroupKeySpace inner, Action onOwnersAsked) : IGroupKeySpace
    {
        public IReadOnlyList<string> Patterns(string group, MoveSide side) => inner.Patterns(group, side);

        public IReadOnlyList<string> PatternsOf(string group, string cacheId, MoveSide side) => inner.PatternsOf(group, cacheId, side);

        public IReadOnlyList<string> OwnersOf(string group, MoveSide side, string key)
        {
            onOwnersAsked();
            return inner.OwnersOf(group, side, key);
        }

        public bool HasValidDestination(string group, MoveSide side, string cacheId, string key) => inner.HasValidDestination(group, side, cacheId, key);

        public bool IsMovable(string group, MoveSide side, string cacheId, string key, TimeSpan? timeToLive) => inner.IsMovable(group, side, cacheId, key, timeToLive);

        public KeyMoveKind KindOf(string group, string cacheId) => inner.KindOf(group, cacheId);

        public TimeSpan? MaxSourceTtl(string group, string cacheId) => inner.MaxSourceTtl(group, cacheId);

        public bool HasHashCache(string group) => inner.HasHashCache(group);

        public string? CallerKeyOf(string group, MoveSide side, string cacheId, string key) => inner.CallerKeyOf(group, side, cacheId, key);

        public TimeSpan MoveMinTtl(string group, string cacheId) => inner.MoveMinTtl(group, cacheId);
    }
}
