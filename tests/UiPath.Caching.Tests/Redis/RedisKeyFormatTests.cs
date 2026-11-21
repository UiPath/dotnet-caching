using System.Reflection;
using System.Text;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.Redis;

public class RedisKeyFormatTests
{
    private const string App = "MyApp";

    private static readonly string[] Keyspaces = ["s", "h", "se", "ps", "st", "mg", "mx"];

    private static readonly char[] Alphabet = "ab:${}%_-.Zé日ß/".ToCharArray();

    public static TheoryData<char, bool> DerivedFormats => new()
    {
        { ':', false }, { ':', true }, { '$', false }, { '$', true }, { 'B', false }, { '|', true }, { '{', false },
    };

    [Theory]
    [MemberData(nameof(DerivedFormats))]
    public void Golden_derived_format_builds_the_bytes_of_the_existing_strategies(char separator, bool shardKey)
    {
        var format = RedisKeyFormat.Derive(separator, shardKey);
        foreach (var keyspace in Keyspaces)
        {
            var prefix = string.Join(separator, App, keyspace);
            var strategy = shardKey ? new ShardPrefixRedisKeyStrategy(prefix, separator) : new PrefixRedisKeyStrategy(prefix, separator);
            foreach (var name in Names(seed: separator * 31 + keyspace.Length, shardKey))
            {
                var key = new CacheKey(name, CacheKeyCasing.Sensitive);
                ((byte[])format.Build(App, keyspace, key.Name)!).Should().Equal((byte[])strategy.GetRedisKey(key)!);
            }
        }
    }

    [Fact]
    public void Default_factory_without_a_key_format_returns_the_existing_strategy_types()
    {
        var options = new CacheOptions { AppShortName = App };
        new DefaultRedisKeyStrategyFactory().Create(options, "s").Should().BeOfType<PrefixRedisKeyStrategy>();
#pragma warning disable CS0618
        options.ShardKeyEnabled = true;
#pragma warning restore CS0618
        new DefaultRedisKeyStrategyFactory().Create(options, "s").Should().BeOfType<ShardPrefixRedisKeyStrategy>();
    }

    [Theory]
    [InlineData(':', false, "1")]
    [InlineData('$', false, "2")]
    [InlineData(':', true, "1t")]
    [InlineData('|', false, "u007c")]
    [InlineData('|', true, "u007ct")]
    public void Ids_are_stable(char separator, bool shardKey, string id)
    {
        RedisKeyFormat.Create(separator, shardKey).Id.Should().Be(id);
        RedisKeyFormat.Create(':', false).Should().BeSameAs(RedisKeyFormat.Colon);
        RedisKeyFormat.Create('$', false).Should().BeSameAs(RedisKeyFormat.Dollar);
    }

    [Theory]
    [InlineData('{')]
    [InlineData('}')]
    [InlineData(' ')]
    [InlineData('\ud800')]
    public void Create_refuses_a_brace_whitespace_or_surrogate_separator(char separator)
    {
        var act = () => RedisKeyFormat.Create(separator, shardKey: false);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void T_S2_1_round_trip_and_key_map_equals_build_in_the_other_format(bool shardKey)
    {
        var from = RedisKeyFormat.Create(':', shardKey);
        var to = RedisKeyFormat.Create('$', shardKey);
        var map = new RedisKeyMap(from, to);
        foreach (var keyspace in new[] { "s", "h" })
        {
            foreach (var name in Names(seed: shardKey ? 7 : 3, shardKey))
            {
                var key = from.Build(App, keyspace, name);
                from.TryParse(key, App, keyspace, out var parsed).Should().BeTrue(name);
                from.Build(App, keyspace, parsed!).Should().Be(key);

                map.TryMap(key, App, keyspace, out var mapped).Should().BeTrue();
                mapped.Should().Be(to.Build(App, keyspace, name));
                to.TryParse(mapped, App, keyspace, out var back).Should().BeTrue();
                back.Should().Be(parsed);
            }
        }
    }

    [Theory]
    [InlineData("{t}rest")]
    [InlineData("pre{t}")]
    [InlineData("a{t}b{u}c")]
    [InlineData("plain")]
    public void T_S2_1_tagged_keys_keep_their_slot_across_formats(string name)
    {
        using var multiplexer = ClusterModeMultiplexer();
        var colon = RedisKeyFormat.Create(':', shardKey: true).Build(App, "s", name);
        var dollar = RedisKeyFormat.Create('$', shardKey: true).Build(App, "s", name);
        multiplexer.GetHashSlot(colon).Should().Be(multiplexer.GetHashSlot(dollar));
    }

    [Theory]
    [InlineData("a{}b")]
    [InlineData("a{b")]
    [InlineData("a}b")]
    [InlineData("")]
    public void T_S2_1_tagged_format_rejects_empty_or_unmatched_braces(string name)
    {
        var act = () => RedisKeyFormat.Create(':', shardKey: true).Build(App, "s", name);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void T_S2_1_parse_strips_the_exact_prefix_and_never_splits()
    {
        var format = RedisKeyFormat.Colon;
        format.TryParse("myapp:s:a:b$c", App, "s", out var name).Should().BeTrue();
        name.Should().Be("a:b$c");
        format.TryParse("MyApp:s:x", App, "s", out _).Should().BeFalse("the prefix compare is byte exact");
        format.TryParse("myapp:h:x", App, "s", out _).Should().BeFalse();
        format.TryParse("myapp$s$x", App, "s", out _).Should().BeFalse();
        format.TryParse(new RedisKey(), App, "s", out _).Should().BeFalse();
        format.TryParse(Encoding.UTF8.GetBytes("myapp:s:").Concat(new byte[] { 0xff, 0xfe }).ToArray(), App, "s", out _)
            .Should().BeFalse("bytes that are not UTF-8 do not round trip");
        RedisKeyFormat.Create(':', shardKey: true).TryParse("myapp:s:untagged", App, "s", out _)
            .Should().BeFalse("a tagged format never builds an untagged name");
    }

    [Fact]
    public void T_S2_1_key_map_refuses_formats_with_different_tag_modes()
    {
        var act = () => new RedisKeyMap(RedisKeyFormat.Colon, RedisKeyFormat.Create('$', shardKey: true));
        act.Should().Throw<ArgumentException>().WithMessage("*tag mode*");
    }

    [Fact]
    public void T_S2_2_colon_and_dollar_are_prefix_free_over_every_keyspace()
    {
        var act = () => RedisKeyFormatRules.EnsurePrefixFree(App, [RedisKeyFormat.Colon, RedisKeyFormat.Dollar], RedisKeyFormatRules.Keyspaces);
        act.Should().NotThrow();
        RedisKeyFormatRules.Keyspaces.Should().Contain(["s", "h", "se", "ps", "st", "mg", "mx"]);
    }

    [Fact]
    public void T_S2_2_same_prefix_with_different_tag_modes_is_refused()
    {
        var act = () => RedisKeyFormatRules.EnsurePrefixFree(
            App, [RedisKeyFormat.Colon, RedisKeyFormat.Create(':', shardKey: true)], RedisKeyFormatRules.Keyspaces);
        act.Should().Throw<InvalidOperationException>().WithMessage("*'1'*'1t'*different tag modes*");
    }

    [Fact]
    public void T_S2_2_a_prefix_that_starts_another_is_refused_and_the_pair_named()
    {
        // With 'e' as separator, "myappese" (keyspace s) starts "myappesee" (keyspace se).
        var act = () => RedisKeyFormatRules.EnsurePrefixFree(App, [RedisKeyFormat.Create('e', shardKey: false)], RedisKeyFormatRules.Keyspaces);
        act.Should().Throw<InvalidOperationException>().WithMessage("*keyspace 's'*keyspace 'se'*one starts the other*");
    }

    [Theory]
    [InlineData("my{app")]
    [InlineData("my}app")]
    public void T_S2_2_app_names_with_braces_are_refused(string app)
    {
        var act = () => RedisKeyFormatRules.EnsurePrefixFree(app, [RedisKeyFormat.Colon], ["s"]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*braces*");
    }

    [Fact]
    public void T_S2_2_a_derived_format_with_a_brace_separator_is_refused_for_a_move()
    {
        var act = () => RedisKeyFormatRules.EnsurePrefixFree(App, [RedisKeyFormat.Derive('{', shardKey: false)], ["s"]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*braces*");
    }

    [Theory]
    [InlineData(':')]
    [InlineData('$')]
    public void T_S2_3_only_cache_entries_are_movable(char nameSeparator)
    {
        var format = RedisKeyFormat.Colon;
        bool Movable(string keyspace, string name) =>
            RedisKeyFormatRules.IsMovable(format, format.Build(App, keyspace, name), App, keyspace, nameSeparator);

        Movable("s", "user:42").Should().BeTrue();
        Movable("h", "user:42").Should().BeTrue();
        foreach (var keyspace in new[] { "ps", "st", "se", "mg", "mx" })
        {
            Movable(keyspace, "user:42").Should().BeFalse(keyspace);
        }

        Movable("s", string.Join(nameSeparator, "Caching", "Nodes")).Should().BeFalse("node list");
        Movable("s", string.Join(nameSeparator, "RedisStreamHealthMaintainer", "Lock2")).Should().BeFalse("maintainer lock");
        Movable("h", string.Join(nameSeparator, "caching", "meta", "stream") + "$myapp:st:topic$").Should().BeFalse("quarantine");
        Movable("h", "user:42:stg:abc").Should().BeFalse("staging");
        RedisKeyFormatRules.IsMovable(format, RedisKeyFormat.Dollar.Build(App, "s", "user:42"), App, "s", nameSeparator)
            .Should().BeFalse("another format's key");
    }

    [Fact]
    public void T_S2_3_library_key_names_match_the_keys_the_library_builds()
    {
        var options = new CacheOptions { AppShortName = App, Separator = '$' };
        var key = UiPath.Caching.Negotiation.NodeListStore.KeyFor(options, new RedisCacheOptions());
        RedisKeyFormatRules.IsMovable(RedisKeyFormat.Dollar, key, App, "s", '$').Should().BeFalse();
        RedisKeyFormat.Dollar.TryParse(key, App, "s", out var name).Should().BeTrue();
        name.Should().BeEquivalentTo(LibraryKeyNames.NodeList('$'));
    }

    [Theory]
    [InlineData("v3")]
    [InlineData("1.2")]
    public void T_S2_3_versions_classify_live_and_dead_names_without_parsing(string version)
    {
        RedisKeyFormatRules.EnsureValidVersion(version);
        string[] live = [version, "v9"];
        RedisKeyFormatRules.IsLiveVersion($"{version}:key", live).Should().BeTrue();
        RedisKeyFormatRules.IsLiveVersion("v9:key", live).Should().BeTrue();
        RedisKeyFormatRules.IsLiveVersion("v1:key", live).Should().BeFalse("dead version");
        RedisKeyFormatRules.IsLiveVersion($"{version}x:key", live).Should().BeFalse();
        RedisKeyFormatRules.IsLiveVersion(version, live).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("v:1")]
    [InlineData("v{1}")]
    [InlineData("v@1")]
    [InlineData("v+1")]
    [InlineData("v=1")]
    public void T_S2_3_invalid_versions_are_refused(string version)
    {
        var act = () => RedisKeyFormatRules.EnsureValidVersion(version);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void T_S2_13_a_reserved_keyspace_is_refused_unless_pinned()
    {
        var reserved = new[] { Reserved("s"), Reserved("se"), Reserved("q") };
        var refused = () => RedisKeyFormatRules.EnsureKeyspacesSupported(reserved, []);
        refused.Should().Throw<InvalidOperationException>().WithMessage("unsupported_keyspace: 'q'*");
        var pinned = () => RedisKeyFormatRules.EnsureKeyspacesSupported(reserved, ["q"]);
        pinned.Should().NotThrow();
    }

    private static IReservedRedisKeyspace Reserved(string keyspace)
    {
        var reserved = Substitute.For<IReservedRedisKeyspace>();
        reserved.Keyspace.Returns(keyspace);
        reserved.Owner.Returns("test");
        return reserved;
    }

    private static IEnumerable<string> Names(int seed, bool shardKey)
    {
        var random = new Random(seed);
        string[] fixedNames = ["a", "user:42", "a$b:c", "日本:é", "{t}", "{t}x", "x{t}", "{a}{b}", "%7B"];
        foreach (var name in fixedNames)
        {
            yield return name;
        }

        for (var i = 0; i < 300; i++)
        {
            var chars = new char[random.Next(1, 12)];
            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = Alphabet[random.Next(Alphabet.Length)];
            }

            var name = new string(chars);
            if (!shardKey || IsTaggable(name))
            {
                yield return name;
            }
        }
    }

    private static bool IsTaggable(string name)
    {
        var open = name.IndexOf('{');
        var hasTag = open >= 0 && name.IndexOf('}', open + 1) > open + 1;
        return hasTag || (name.Length > 0 && name.IndexOfAny(['{', '}']) < 0);
    }

    private static ConnectionMultiplexer ClusterModeMultiplexer()
    {
        var multiplexer = ConnectionMultiplexer.Connect("127.0.0.1:1,abortConnect=false,connectTimeout=100,connectRetry=0");
        var strategy = typeof(ConnectionMultiplexer).GetProperty("ServerSelectionStrategy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(multiplexer)!;
        strategy.GetType().GetProperty("ServerType")!.SetValue(strategy, ServerType.Cluster);
        return multiplexer;
    }
}
