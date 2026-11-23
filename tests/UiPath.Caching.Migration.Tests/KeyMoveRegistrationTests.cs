using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration.Tests;

// UseKeyMove: what a build accepts, each rule that refuses, and the traits and key map the setup carries.
public class KeyMoveRegistrationTests
{
    private static StateRecord Fresh { get; } = new("S0", "none", 1, "id1", true, 0, string.Empty, null, "a", "cs", string.Empty, string.Empty, string.Empty, string.Empty);

    [Fact]
    public static void A_valid_registration_builds_a_setup_with_the_traits_the_driver_reads()
    {
        var setup = Builder(Routed("a", "g1"), Routed("b", "g1")).UseKeyMove(new Stage(), p => p
            .UseLibraryKeyPrefix("lib:")
            .AttestReleaseFloor()
            .AddGroup("g1", listed: true)
            .AddCache(Cache("a", noOrigin: true, ttl: TimeSpan.FromHours(1)))
            .AddCache(Cache("b", refill: true, ttl: TimeSpan.FromHours(3)))).BuildKeyMove()!;

        setup.LibraryPrefix.Should().Be("lib:");
        setup.Groups.Should().ContainSingle().Which.Should().Match<GroupRegistration>(g => g.Name == "g1" && g.Flagged && g.Listed && g.CacheIds!.SequenceEqual(new[] { "a", "b" }));
        setup.Facts.ReleaseFloorAttested.Should().BeTrue();
        setup.Facts.HasNoOrigin("g1", ["a", "b"]).Should().BeTrue("a NoOrigin cache takes the S2 barrier");
        setup.Facts.HasNoOrigin("g1", ["b"]).Should().BeFalse();
        setup.Facts.AllAcceptRefill("g1", ["b"]).Should().BeTrue();
        setup.Facts.AllAcceptRefill("g1", ["a", "b"]).Should().BeFalse();
        setup.Facts.LongestSourceTtl("g1").Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public static void The_facts_no_registration_holds_are_unknown_until_attested_and_an_unknown_fact_refuses_Start()
    {
        var bare = Valid().BuildKeyMove()!.Facts;
        bare.FormatSettled("g1", Fresh).Should().BeFalse();
        bare.NodeListComplete("g1", Fresh).Should().BeFalse();
        bare.MemoryFits("g1", "report-1", 1, 0.2).Should().BeFalse();
        bare.ReleaseFloorAttested.Should().BeFalse();

        var attested = Builder(Routed("a", "g1")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a")).AttestFormatSettled("g1", "e1").AttestNodeListComplete("g1", "e2").AttestMemoryFits("g1", "report-1")).BuildKeyMove()!.Facts;
        attested.FormatSettled("g1", Fresh).Should().BeTrue();
        attested.NodeListComplete("g1", Fresh).Should().BeTrue();
        attested.MemoryFits("g1", "report-1", 1, 0.2).Should().BeTrue();
        attested.MemoryFits("g1", "report-2", 1, 0.2).Should().BeFalse("a newer dry-run report is not the one attested");
        attested.FormatSettled("g2", Fresh).Should().BeFalse("another group");
    }

    [Fact]
    public static void The_key_map_uses_the_router_registration_for_the_in_place_side_and_the_registered_one_for_the_native_side()
    {
        var setup = Valid().BuildKeyMove()!;

        setup.KeySpace.Patterns("g1", MoveSide.Legacy).Should().Equal("l:a:*");
        setup.KeySpace.Patterns("g1", MoveSide.Native).Should().Equal("lib:n:a:*");
        setup.KeySpace.OwnersOf("g1", MoveSide.Legacy, "l:a:k").Should().Equal("a");
        setup.KeySpace.IsMovable("g1", MoveSide.Legacy, "a", "l:a:k", null).Should().BeTrue("the router's own key format round-trips");
        setup.KeySpace.IsMovable("g1", MoveSide.Native, "a", "lib:n:a:k", null).Should().BeTrue();
        setup.KeySpace.OwnersOf("g1", MoveSide.Native, "l:a:k").Should().BeEmpty();
    }

    [Fact]
    public static void Without_UseKeyMove_nothing_is_built()
    {
        Builder(Routed("a", "g1")).BuildKeyMove().Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public static void Each_rule_refuses_at_build(string rule, Func<RouterBuilder> make, string message)
    {
        var act = () => make().BuildKeyMove();

        act.Should().Throw<Exception>(rule).WithMessage($"*{message}*");
    }

    public static TheoryData<string, Func<RouterBuilder>, string> Refusals() => new()
    {
        { "no prefix", () => Using(p => p.AddGroup("g1").AddCache(Cache("a"))), "UseLibraryKeyPrefix" },
        { "prefix with a brace", () => Using(p => p.UseLibraryKeyPrefix("a{b:").AddGroup("g1").AddCache(Cache("a"))), "brace" },
        { "prefix with a glob character", () => Using(p => p.UseLibraryKeyPrefix("app[1]:").AddGroup("g1").AddCache(Cache("a"))), "glob character" },
        { "prefix with a star", () => Using(p => p.UseLibraryKeyPrefix("app*:").AddGroup("g1").AddCache(Cache("a"))), "glob character" },
        { "no group", () => Using(p => p.UseLibraryKeyPrefix("lib:")), "no group" },
        { "group name with a forbidden character", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g+1").AddCache(Cache("a", group: "g+1"))), "cannot take part" },
        { "group added twice", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddGroup("g1").AddCache(Cache("a"))), "added twice" },
        { "cache name with a forbidden character", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a=b"))), "cannot take part" },
        { "cache in two groups", () => Builder(Routed("a", "g1")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddGroup("g2").AddCache(Cache("a")).AddCache(Cache("a", group: "g2"))), "added twice" },
        { "cache added twice with other patterns", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a")).AddCache(Cache("a", nativePattern: "lib:n:other:*", owns: _ => true))), "added twice" },
        { "native pattern outside the namespace", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a", nativePattern: "n:*"))), "outside the application's namespace" },
        { "formatted native key outside the namespace", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a", formatNative: key => "n:" + key))), "outside the application's namespace" },
        { "native pattern that can overlap an excluded keyspace", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a")).ExcludeKeyspace("lib:n:*")), "can overlap the excluded keyspace" },
        { "native pattern under a wider excluded keyspace", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a")).ExcludeKeyspace("lib:*")), "can overlap the excluded keyspace" },
        { "in-place pattern that can overlap an excluded keyspace", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a")).ExcludeKeyspace("l:a:k*")), "in-place pattern" },
        { "in-place pattern that is a bare wildcard", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a", inPlacePattern: "*"))), "starts with a wildcard" },
        { "router cache of the group left out of AddCache", () => Builder(Routed("a", "g1"), Routed("forgotten", "g1")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a"))), "not registered with AddCache" },
        { "pattern shared across groups without predicates", () => Builder(Routed("a", "g1"), Routed("b", "g2")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddGroup("g2").AddCache(Cache("a", nativePattern: "lib:n:*")).AddCache(Cache("b", group: "g2", nativePattern: "lib:n:*"))), "ownership predicate" },
        { "second UseKeyMove", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a"))).UseKeyMove(new Stage(), _ => { }), "already called" },
        { "attested twice", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a")).AttestFormatSettled("g1", "e1").AttestFormatSettled("g1", "e2")), "attested twice" },
        { "evidence id with a comma", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a")).AttestNodeListComplete("g1", "a,b")), "comma, a colon or whitespace" },
        { "null cache name", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache(null!))), "cannot take part" },
        { "null parse delegate", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(NullParse())), "needs ParseInPlaceKey" },
        { "attestation for a group that was not added", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a")).AttestMemoryFits("ghost", "r")), "which was not added" },
        { "cache group not added", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a", group: "other"))), "was not added" },
        { "cache not routed", () => Builder().UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a"))), "not registered with the router" },
        { "cache routed in another group", () => Builder(Routed("a", "g2")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a"))), "not registered with the router in the group 'g1'" },
        { "group without a cache", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddGroup("empty").AddCache(Cache("a"))), "has no cache" },
        { "empty scan pattern", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a", nativePattern: " "))), "scan pattern" },
        { "source life not positive", () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a", ttl: TimeSpan.Zero))), "MaxSourceTtl" },
        { "caches that share a pattern without predicates", () => Builder(Routed("a", "g1"), Routed("b", "g1")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a", nativePattern: "lib:n:*")).AddCache(Cache("b", nativePattern: "lib:n:*"))), "ownership predicate" },
    };

    [Fact]
    public static void Caches_that_share_a_pattern_are_accepted_when_each_has_a_predicate()
    {
        var setup = Builder(Routed("a", "g1"), Routed("b", "g1")).UseKeyMove(new Stage(), p => p
            .UseLibraryKeyPrefix("lib:")
            .AddGroup("g1")
            .AddCache(Cache("a", nativePattern: "lib:n:*", owns: k => k.StartsWith("lib:n:a", StringComparison.Ordinal)))
            .AddCache(Cache("b", nativePattern: "lib:n:*", owns: k => k.StartsWith("lib:n:b", StringComparison.Ordinal)))).BuildKeyMove()!;

        setup.KeySpace.OwnersOf("g1", MoveSide.Native, "lib:n:b1").Should().Equal("b");
    }

    [Fact]
    public static void The_arguments_are_checked_when_they_are_given()
    {
        var builder = new RouterBuilder();

        ((Action)(() => builder.UseKeyMove(null!, _ => { }))).Should().Throw<ArgumentNullException>();
        ((Action)(() => builder.UseKeyMove(new Stage(), null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new KeyMoveBuilder().AddGroup(" "))).Should().Throw<ArgumentException>();
        ((Action)(() => new KeyMoveBuilder().AddCache(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new KeyMoveBuilder().UseLibraryKeyPrefix(string.Empty))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public static void An_invalid_registration_fails_the_host_build_and_no_registration_changes_nothing()
    {
        var services = new ServiceCollection().AddLogging();
        var configuration = new ConfigurationBuilder().Build();

        var act = () => services.AddCachingRouter(
            configuration,
            router => router
                .UseLibraryWhen(_ => true)
                .UseEnvelope(new Support.LegacyEnvelopeProxy())
                .Add(Router("a", "g1"))
                .UseKeyMove(new Stage(), p => p.AddGroup("g1").AddCache(Cache("a"))));

        act.Should().Throw<InvalidOperationException>().WithMessage("*UseLibraryKeyPrefix*");
    }

    [Fact]
    public static void AtMostOnce_implies_NoOrigin_so_the_group_takes_the_S2_barrier()
    {
        var setup = Builder(Routed("a", "g1")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a", atMostOnce: true))).BuildKeyMove()!;

        setup.Facts.HasNoOrigin("g1", ["a"]).Should().BeTrue();
    }

    [Fact]
    public static void An_excluded_keyspace_that_cannot_overlap_a_moving_pattern_is_accepted_and_reaches_the_key_space()
    {
        var setup = Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a")).ExcludeKeyspace("lib:s:*").ExcludeKeyspace("lib:h:*")).BuildKeyMove()!;

        setup.KeySpace.OwnersOf("g1", MoveSide.Native, "lib:n:a:k").Should().Equal("a");
        setup.KeySpace.OwnersOf("g1", MoveSide.Native, "lib:s:a:k").Should().BeEmpty();
    }

    [Fact]
    public static void An_excluded_pattern_is_required_to_say_something()
    {
        ((Action)(() => new KeyMoveBuilder().ExcludeKeyspace(" "))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public static void A_format_function_that_throws_on_a_probe_key_is_judged_at_use_time_not_refused()
    {
        var setup = Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a", formatNative: key => "lib:n:a:" + long.Parse(key, System.Globalization.CultureInfo.InvariantCulture)))).BuildKeyMove();

        setup.Should().NotBeNull();
    }

    [Fact]
    public static void A_function_that_returns_a_key_outside_the_namespace_on_a_probe_it_takes_is_still_refused()
    {
        var act = () => Using(p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a", formatNative: key => key == "k" ? "other:" + key : "lib:n:a:" + key))).BuildKeyMove();

        act.Should().Throw<InvalidOperationException>().WithMessage("*outside the application's namespace*");
    }

    [Fact]
    public static void A_throwing_alert_handler_is_swallowed_and_the_others_still_get_their_alerts()
    {
        var seen = 0;
        var setup = Builder(Routed("a", "g1")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a")).OnAlert(_ =>
        {
            seen++;
            throw new InvalidOperationException("handler failed");
        })).BuildKeyMove()!;

        var act = () => setup.Alert!(new KeyMoveAlert("g1", "code", "detail"));

        act.Should().NotThrow();
        seen.Should().Be(1);
    }

    [Fact]
    public static void An_attestation_needs_a_group_and_an_evidence_id()
    {
        var builder = new KeyMoveBuilder();

        ((Action)(() => builder.AttestFormatSettled(" ", "e"))).Should().Throw<ArgumentException>();
        ((Action)(() => builder.AttestNodeListComplete("g1", string.Empty))).Should().Throw<ArgumentException>();
        ((Action)(() => builder.AttestMemoryFits("g1", " "))).Should().Throw<ArgumentException>();
    }

    private static KeyMoveCacheRegistration NullParse() => new()
    {
        Name = "a",
        Group = "g1",
        InPlacePattern = "l:a:*",
        ParseInPlaceKey = null!,
        NativePattern = "lib:n:a:*",
        FormatNativeKey = key => "lib:n:a:" + key,
        ParseNativeKey = _ => null,
    };

    private static RouterBuilder Valid() => Builder(Routed("a", "g1")).UseKeyMove(new Stage(), p => p.UseLibraryKeyPrefix("lib:").AddGroup("g1").AddCache(Cache("a")));

    private static RouterBuilder Using(Action<KeyMoveBuilder> configure) => Builder(Routed("a", "g1")).UseKeyMove(new Stage(), configure);

    private static RouterBuilder Builder(params RouterCacheRegistration<string>[] routed)
    {
        var builder = new RouterBuilder();
        foreach (var registration in routed)
        {
            builder.Add(registration);
        }

        return builder;
    }

    private static RouterCacheRegistration<string> Routed(string name, string group) => Router(name, group);

    private static RouterCacheRegistration<string> Router(string name, string group) =>
        new() { Name = name, Group = group, Source = new Source(), RedisKey = key => $"l:{name}:{key}" };

    private static KeyMoveCacheRegistration Cache(string name, string group = "g1", bool noOrigin = false, bool refill = false, TimeSpan? ttl = null, string? inPlacePattern = null, string? nativePattern = null, Func<string, bool>? owns = null, bool atMostOnce = false, Func<string, string>? formatNative = null) => new()
    {
        Name = name,
        Group = group,
        NoOrigin = noOrigin,
        AcceptRefill = refill,
        MaxSourceTtl = ttl,
        InPlacePattern = inPlacePattern ?? $"l:{name}:*",
        ParseInPlaceKey = key => key.StartsWith($"l:{name}:", StringComparison.Ordinal) ? key[(3 + name.Length)..] : null,
        AtMostOnce = atMostOnce,
        NativePattern = nativePattern ?? $"lib:n:{name}:*",
        FormatNativeKey = formatNative ?? (key => $"lib:n:{name}:{key}"),
        ParseNativeKey = key => key.StartsWith($"lib:n:{name}:", StringComparison.Ordinal) ? key[$"lib:n:{name}:".Length..] : null,
        OwnsNativeKey = owns,
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
}
