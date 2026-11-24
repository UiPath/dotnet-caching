using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// What the inventory is told about a hash: the field count decides, with the cap of the registration, the origin of the cache and the nulls of the other side, and the read of the client agrees with it.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashClassesTests(RedisFixture fixture)
{
    private const int Cap = 4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(Cap, false, false, false)]
    [InlineData(Cap + 1, false, false, true)]
    [InlineData(Cap + 1, false, true, false)]
    [InlineData(0, false, false, true)]
    [InlineData(0, true, false, false)]
    public void A_hash_is_droppable_by_its_field_count_the_other_sides_nulls_and_the_origin(long length, bool otherCachesNulls, bool noOrigin, bool expected)
    {
        var classes = new KeyMoveValueClasses();
        classes.AddHash("a", Binding(new ItemHashCodec(), new ItemHashCodec(otherCachesNulls, true), noOrigin));

        classes.IsDroppable("a", MoveSide.Legacy, length).Should().Be(expected);
    }

    [Fact]
    public void The_native_side_is_judged_by_the_in_place_codecs_nulls_and_a_cache_with_no_binding_is_never_droppable()
    {
        var classes = new KeyMoveValueClasses();
        classes.AddHash("a", Binding(new ItemHashCodec(true), new ItemHashCodec(false, true), false));

        classes.IsDroppable("a", MoveSide.Native, 0).Should().BeFalse("L caches nulls, so an empty hash in N moves back");
        classes.IsDroppable("a", MoveSide.Native, Cap + 1).Should().BeTrue();
        classes.IsDroppable("other", MoveSide.Legacy, Cap + 1).Should().BeFalse();
    }

    [Fact]
    public void A_string_cache_and_a_hash_cache_of_one_inventory_are_judged_each_by_its_own_rule()
    {
        var classes = new KeyMoveValueClasses();
        classes.Add("s", new KeyMoveBinding<Item>(ItemCodec.Instance, ItemCodec.Instance, k => k, k => k, false));
        classes.AddHash("h", Binding(new ItemHashCodec(), new ItemHashCodec(false, true), false));

        classes.IsDroppable("s", MoveSide.Legacy, Cap + 1).Should().BeFalse("a string of five bytes is no large value");
        classes.IsDroppable("h", MoveSide.Legacy, Cap + 1).Should().BeTrue();
    }

    [Fact]
    public void BindHash_tells_the_inventory_the_cap_of_the_registration_and_the_binding_is_reported_by_the_binder()
    {
        var classes = new KeyMoveValueClasses();
        var cache = new KeyMoveCache("items", "g", false, false, false, null, "p*", k => k, k => k, "n*", k => k, k => k, Kind: KeyMoveKind.Hash);
        var binder = new KeyMoveBinder(new Strings(), new Strings(), [cache], new KeyMoveOptions { MaxHashFields = 9 }, classes, new Hashes(false), new Hashes(false));

        var bound = binder.BindHash<Item>("items", 6)!;
        var defaulted = binder.BindHash<Item>("items")!;

        bound.MaxHashFields.Should().Be(6);
        defaulted.MaxHashFields.Should().Be(9);
        classes.IsDroppable("items", MoveSide.Legacy, 9).Should().BeFalse("the last binding is the one the inventory judges by");
        classes.IsDroppable("items", MoveSide.Legacy, 10).Should().BeTrue();
        binder.BindHash<Item>("items", 6);
        classes.IsDroppable("items", MoveSide.Legacy, 7).Should().BeTrue();
        classes.IsDroppable("items", MoveSide.Legacy, 6).Should().BeFalse();
    }

    [Fact]
    public void A_registration_that_says_it_holds_hashes_makes_the_key_space_say_so_and_one_that_does_not_holds_strings()
    {
        var setup = new RouterBuilder()
            .Add(new RouterCacheRegistration<string> { Name = "a", Group = "g1", Source = new Source(), RedisKey = key => $"l:a:{key}" })
            .Add(new RouterCacheRegistration<string> { Name = "b", Group = "g1", Source = new Source(), RedisKey = key => $"l:b:{key}" })
            .UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Registration("a", KeyMoveKind.Hash)).AddCache(Registration("b", KeyMoveKind.String)))
            .BuildKeyMove()!;

        setup.KeySpace.KindOf("g1", "a").Should().Be(KeyMoveKind.Hash);
        setup.KeySpace.KindOf("g1", "b").Should().Be(KeyMoveKind.String);
        setup.KeySpace.KindOf("g1", "unknown").Should().Be(KeyMoveKind.String);
        setup.Caches.Single(c => c.Name == "a").Kind.Should().Be(KeyMoveKind.Hash);
    }

    // The inventory and the read judge a hash by one predicate: what the inventory calls droppable is what a read in S3 removes, and nothing else is.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_inventorys_verdict_for_every_length_around_the_cap_is_what_a_read_in_S3_does_to_the_hash(bool noOrigin)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: noOrigin, maxFields: Cap);
        var classes = new KeyMoveValueClasses();
        classes.AddHash(kit.Kit.Cid, new KeyMoveHashBinding<Item>(new ItemHashCodec(false, false, Cap), new ItemHashCodec(false, true, Cap), kit.L, kit.N, noOrigin, Cap));
        await kit.Kit.SetStateAsync("S3");
        foreach (var length in new[] { 1, Cap - 1, Cap, Cap + 1, Cap + 3 })
        {
            var key = $"len{length}";
            await kit.Kit.PrepareAsync(key);
            await kit.SetAsync(kit.L(key), Enumerable.Range(0, length).ToDictionary(i => $"f{i}", i => $"v{i}"));

            await kit.Client.ReadAsync(key, null, Ct);

            var removed = !await kit.Kit.ExistsAsync(kit.L(key)) && !await kit.Kit.ExistsAsync(kit.N(key));
            classes.IsDroppable(kit.Kit.Cid, MoveSide.Legacy, length).Should().Be(removed, $"a hash of {length} fields");
        }
    }

    private static KeyMoveHashBinding<Item> Binding(IKeyMoveHashCodec<Item> inPlace, IKeyMoveHashCodec<Item> native, bool noOrigin) =>
        new(inPlace, native, k => k, k => k, noOrigin, Cap);

    private static KeyMoveCacheRegistration Registration(string name, KeyMoveKind kind) => new()
    {
        Name = name,
        Group = "g1",
        Kind = kind,
        InPlacePattern = $"l:{name}:*",
        ParseInPlaceKey = key => key.StartsWith($"l:{name}:", StringComparison.Ordinal) ? key[(3 + name.Length)..] : null,
        NativePattern = $"lib:n:{name}:*",
        FormatNativeKey = key => $"lib:n:{name}:{key}",
        ParseNativeKey = key => key.StartsWith($"lib:n:{name}:", StringComparison.Ordinal) ? key[$"lib:n:{name}:".Length..] : null,
    };

    private sealed class Stage : IMigrationStageSource
    {
        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken) => ValueTask.FromResult<int?>(2);
    }

    private sealed class Source : ISourceCache<string>
    {
        public ValueTask<SourceRead<string>> GetAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<WriteResult> SetAsync(string key, string value, CacheExpiry expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<AddResult> TryAddAsync(string key, string value, CacheExpiry expiry, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Strings : IByteCodecCache
    {
        public bool CachesNulls => false;

        public byte[] Encode<T>(T value, CachePolicy? policy) => throw new NotSupportedException();

        public (SnapshotOutcome Outcome, T? Value) Decode<T>(byte[] stored, CachePolicy? policy, bool quiet = false) => throw new NotSupportedException();
    }

    private sealed class Hashes(bool cachesNulls) : IByteHashCodecCache
    {
        public bool CachesNulls => cachesNulls;

        public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields<T>(IDictionary<string, T?> values, IDictionary<string, string?>? metadata, CachePolicy? policy) => throw new NotSupportedException();

        public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeUserFields<T>(IDictionary<string, T?> values, CachePolicy? policy) => throw new NotSupportedException();

        public HashDecode<T> DecodeFields<T>(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored, CachePolicy? policy, bool quiet = false) => throw new NotSupportedException();
    }
}
