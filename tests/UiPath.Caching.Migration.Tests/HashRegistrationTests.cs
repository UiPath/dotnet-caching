using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests;

// AddHash: what a build accepts and refuses, how a hash cache is told from a string cache, and how its library path is put together.
public sealed class HashRegistrationTests : RouterTestBase
{
    private const int Cap = 9;

    [Fact]
    public void GetHash_requires_an_implementation()
    {
        typeof(IRouter).GetMethod(nameof(IRouter.GetHash))!.IsAbstract.Should().BeTrue();
    }

    [Fact]
    public void A_hash_cannot_use_an_excluded_name()
    {
        var builder = new RouterBuilder().Exclude("items", "Managed by the source");
        var add = () => builder.AddHash(Hash("items"));

        add.Should().Throw<ArgumentException>().WithMessage("*items*excluded*");
        builder.Registrations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_nonpositive_default_hash_lifetime_is_refused(int seconds)
    {
        var builder = new RouterBuilder();
        var add = () => builder.AddHash(Hash("items", defaultExpiry: TimeSpan.FromSeconds(seconds)));

        add.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("registration");
        builder.AddHash(Hash("items", defaultExpiry: TimeSpan.FromSeconds(1))).Registrations.Should().ContainSingle();
    }

    [Fact]
    public void A_registration_with_a_missing_part_or_a_window_out_of_range_is_refused()
    {
        var builder = new RouterBuilder();

        var refused = new Action[]
        {
            () => builder.AddHash<Item>(null!),
            () => builder.AddHash(Hash(" ")),
            () => builder.AddHash(Hash("a", group: "")),
            () => builder.AddHash(Hash("a", window: TimeSpan.Zero)),
            () => builder.AddHash(Hash("a", window: TimeSpan.FromDays(2))),
        };

        foreach (var call in refused)
        {
            call.Should().Throw<ArgumentException>();
        }

        builder.Registrations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public void A_local_cap_other_than_zero_is_refused_naming_the_cache(int seconds)
    {
        var builder = new RouterBuilder();

        var add = () => builder.AddHash(Hash("items", localCap: TimeSpan.FromSeconds(seconds)));

        add.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*items*local tier*");
        builder.Registrations.Should().BeEmpty();
        builder.AddHash(Hash("items", localCap: TimeSpan.Zero)).Registrations.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_field_cap_below_one_is_refused_and_one_or_none_is_accepted(int cap)
    {
        var builder = new RouterBuilder();

        var add = () => builder.AddHash(Hash("items", maxFields: cap));

        add.Should().Throw<ArgumentOutOfRangeException>();
        builder.AddHash(Hash("one", maxFields: 1)).AddHash(Hash("none")).Registrations.Should().HaveCount(2);
    }

    [Fact]
    public void One_name_is_registered_once_across_the_string_and_the_hash_forms_in_either_order()
    {
        var hashFirst = new RouterBuilder().AddHash(Hash("x"));
        var stringFirst = new RouterBuilder().Add(Text("x"));

        var text = () => hashFirst.Add(Text("x"));
        var hash = () => stringFirst.AddHash(Hash("x"));
        var twice = () => hashFirst.AddHash(Hash("x"));

        text.Should().Throw<ArgumentException>().WithMessage("*'x'*already*");
        hash.Should().Throw<ArgumentException>().WithMessage("*'x'*already*");
        twice.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task GetHash_returns_the_hash_cache_and_each_form_fails_clearly_on_the_other_kind_of_name()
    {
        var builder = new RouterBuilder().AddHash(Hash("items")).Add(Text("texts"));
        await using var router = new Router(NewCore(), builder.Registrations);

        router.GetHash<Item>("items").Name.Should().Be("items");
        var wrongKind = () => router.Get<Item>("items");
        var wrongName = () => router.GetHash<string>("texts");
        var wrongType = () => router.GetHash<string>("items");
        var unknown = () => router.GetHash<Item>("nope");

        wrongKind.Should().Throw<InvalidOperationException>().WithMessage("*'items'*hash cache*GetHash*");
        wrongName.Should().Throw<InvalidOperationException>().WithMessage("*'texts'*not a hash cache*");
        wrongType.Should().Throw<InvalidOperationException>().WithMessage("*'items'*Item*String*");
        unknown.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void A_hash_cache_of_a_key_move_group_is_a_hash_in_the_inventory_and_a_string_cache_stays_a_string()
    {
        var setup = new RouterBuilder()
            .AddHash(Hash("h", group: "g1"))
            .Add(Text("s", group: "g1"))
            .UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Move("h")).AddCache(Move("s")))
            .BuildKeyMove()!;

        setup.KeySpace.KindOf("g1", "h").Should().Be(KeyMoveKind.Hash);
        setup.KeySpace.KindOf("g1", "s").Should().Be(KeyMoveKind.String);
    }

    [Fact]
    public void A_key_move_registration_whose_kind_disagrees_with_the_router_registration_is_refused()
    {
        var asString = new RouterBuilder().Add(Text("s", group: "g1")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Move("s", KeyMoveKind.Hash)));
        var asHash = new RouterBuilder().AddHash(Hash("h", group: "g1")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Move("h", KeyMoveKind.Hash)));

        asString.Invoking(b => b.BuildKeyMove()).Should().Throw<InvalidOperationException>().WithMessage("*'s'*string cache*kind*");
        asHash.BuildKeyMove()!.KeySpace.KindOf("g1", "h").Should().Be(KeyMoveKind.Hash, "naming the kind the router already knows is no disagreement");
    }

    [Fact]
    public void A_hash_pattern_that_can_overlap_an_excluded_keyspace_is_refused()
    {
        var builder = new RouterBuilder().AddHash(Hash("h", group: "g1")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Move("h")).ExcludeKeyspace("l:h:*"));

        builder.Invoking(b => b.BuildKeyMove()).Should().Throw<InvalidOperationException>().WithMessage("*'h'*excluded keyspace*");
    }

    [Fact]
    public void A_hash_cache_with_no_key_move_registration_builds_no_setup_and_keeps_the_plain_path()
    {
        var builder = new RouterBuilder().AddHash(Hash("h"));
        var cache = new RoutedHashCache<Item>(NewCore(), Hash("h"));
        var parts = HashParts();

        using (var attachment = cache.Prepare(parts))
        {
            attachment.Commit();
        }

        builder.BuildKeyMove().Should().BeNull();
        cache.Library.Should().BeOfType<LibraryHashPathClient<Item>>();
        cache.IsSubscribed.Should().BeTrue();
    }

    [Fact]
    public void The_plain_library_path_is_given_the_hash_cache_and_its_shared_tier()
    {
        var cache = new RoutedHashCache<Item>(NewCore(), Hash("h"));
        var tier = InterceptingProxy.Wrap<ISharedHashTier>(null!, (_, _) => InterceptingProxy.Proceed);
        var store = new FakeMigrationHashCache();

        cache.Prepare(HashParts(store) with { HashTier = tier }).Commit();

        var plain = cache.Library.Should().BeOfType<LibraryHashPathClient<Item>>().Subject;
        plain.Tier.Should().BeSameAs(tier);
    }

    [Fact]
    public void A_router_with_a_hash_cache_and_no_library_hash_cache_cannot_prepare_it_and_a_started_one_is_attached_once()
    {
        var cache = new RoutedHashCache<Item>(NewCore(), Hash("h"));
        var bare = Parts(new FakeMigrationCache());

        var prepare = () => cache.Prepare(bare);

        prepare.Should().Throw<InvalidOperationException>().WithMessage("*'h'*");
        cache.IsSubscribed.Should().BeFalse();
        var first = cache.Prepare(HashParts());
        var second = cache.Prepare(HashParts());
        first.Commit();
        var attached = cache.Library;
        second.Commit();
        second.Dispose();
        cache.Library.Should().BeSameAs(attached, "the first attachment stays");
    }

    [Fact]
    public void The_binding_of_a_hash_cache_carries_the_field_cap_of_its_registration_and_the_default_when_there_is_none()
    {
        var (cappedClient, cappedClasses) = Bound(Hash("items", maxFields: 6));
        var (defaultedClient, defaultedClasses) = Bound(Hash("items"));

        cappedClient.Parts.MaxHashFields.Should().Be(6);
        defaultedClient.Parts.MaxHashFields.Should().Be(Cap);
        cappedClasses.IsDroppable("items", MoveSide.Legacy, 7).Should().BeTrue("the inventory judges by the cap of the binding in force");
        cappedClasses.IsDroppable("items", MoveSide.Legacy, 6).Should().BeFalse();
        defaultedClasses.IsDroppable("items", MoveSide.Legacy, 7).Should().BeFalse();
    }

    [Fact]
    public void A_key_move_hash_cache_with_no_hash_codec_caches_fails_its_preparation_and_the_binder_is_told_nothing()
    {
        var classes = new KeyMoveValueClasses();
        var cache = new KeyMoveCache("items", "g", false, false, false, null, "p*", k => k, k => k, "n*", k => k, k => k, Kind: KeyMoveKind.Hash);
        var binder = new KeyMoveBinder(new Strings(), new Strings(), [cache], classes: classes);
        var routed = new RoutedHashCache<Item>(NewCore(), Hash("items"));

        var prepare = () => routed.Prepare(HashParts() with { Binder = binder, KeyMove = new KeyMove.StubPlane("g", new EpochGate()) });

        prepare.Should().Throw<InvalidOperationException>().WithMessage("*'items'*Redis cache provider*");
        classes.IsDroppable("items", MoveSide.Legacy, 99999).Should().BeFalse();
        routed.IsSubscribed.Should().BeFalse();
    }

    [Fact]
    public void The_library_start_fails_clearly_when_the_stack_hash_cache_is_not_a_shared_tier()
    {
        var local = InterceptingProxy.Wrap<IHashCache>(null!, (_, _) => InterceptingProxy.Proceed);

        var start = () => RouterServiceCollectionExtensions.LibraryHashCacheOf(Root(local), new HashSet<string> { "b", "a" });

        start.Should().Throw<InvalidOperationException>().WithMessage("*(a, b)*no local tier*");
    }

    private static ServiceProvider Root(IHashCache hashCache)
    {
        var factory = InterceptingProxy.Wrap<ICacheFactory>(null!, (method, _) => method.Name == nameof(ICacheFactory.CreateHashCache) ? hashCache : InterceptingProxy.Proceed);
        return new ServiceCollection().AddKeyedSingleton(RouterServiceCollectionExtensions.StackName, factory).BuildServiceProvider();
    }

    private static RouterHashCacheRegistration<Item> Hash(string name, string group = "g", TimeSpan? window = null, TimeSpan? localCap = null, int? maxFields = null, TimeSpan? defaultExpiry = null) => new()
    {
        Name = name,
        Group = group,
        Source = new FakeHashBackend([], new ManualTimeProvider(DateTimeOffset.UnixEpoch)),
        RedisKey = key => $"l:{name}:{key}",
        PendingWindow = window ?? TimeSpan.FromSeconds(30),
        LocalCap = localCap ?? TimeSpan.Zero,
        MaxHashFields = maxFields,
        DefaultExpiry = defaultExpiry,
    };

    private static RouterCacheRegistration<string> Text(string name, string group = "g") => new() { Name = name, Group = group, Source = new TextSource(), RedisKey = key => $"l:{name}:{key}" };

    private static KeyMoveCacheRegistration Move(string name, KeyMoveKind kind = KeyMoveKind.String) => new()
    {
        Name = name,
        Group = "g1",
        InPlacePattern = $"l:{name}:*",
        ParseInPlaceKey = key => key.StartsWith($"l:{name}:", StringComparison.Ordinal) ? key[(3 + name.Length)..] : null,
        NativePattern = $"lib:n:{name}:*",
        FormatNativeKey = key => $"lib:n:{name}:{key}",
        ParseNativeKey = key => key.StartsWith($"lib:n:{name}:", StringComparison.Ordinal) ? key[$"lib:n:{name}:".Length..] : null,
        Kind = kind,
    };

    private RouterCore NewCore() => new(Clock, _ => true, NullLogger.Instance);

    private LibraryParts HashParts(FakeMigrationHashCache? store = null) => Parts(new FakeMigrationCache()) with { HashStore = store ?? new FakeMigrationHashCache() };

    private (KeyMoveHashClient<Item> Client, KeyMoveValueClasses Classes) Bound(RouterHashCacheRegistration<Item> registration)
    {
        var classes = new KeyMoveValueClasses();
        var cache = new KeyMoveCache(registration.Name, "g", false, false, false, null, "p*", k => k, k => k, "n*", k => k, k => k, Kind: KeyMoveKind.Hash);
        var binder = new KeyMoveBinder(new Strings(), new Strings(), [cache], new KeyMoveOptions { MaxHashFields = Cap }, classes, new Hashes(), new Hashes());
        var plane = new KeyMove.StubPlane("g", new EpochGate());
        var routed = new RoutedHashCache<Item>(NewCore(), registration);
        routed.Prepare(HashParts() with { Binder = binder, KeyMove = plane }).Commit();
        return (routed.Library.Should().BeOfType<KeyMoveHashClient<Item>>().Subject, classes);
    }

    private sealed class Stage : IMigrationStageSource
    {
        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken) => ValueTask.FromResult<int?>(2);
    }

    private sealed class TextSource : ISourceCache<string>
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

    private sealed class Hashes : IByteHashCodecCache
    {
        public bool CachesNulls => false;

        public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields<T>(IDictionary<string, T?> values, IDictionary<string, string?>? metadata, CachePolicy? policy) => throw new NotSupportedException();

        public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeUserFields<T>(IDictionary<string, T?> values, CachePolicy? policy) => throw new NotSupportedException();

        public HashDecode<T> DecodeFields<T>(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored, CachePolicy? policy, bool quiet = false) => throw new NotSupportedException();
    }
}
