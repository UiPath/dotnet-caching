using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Redis;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Serialization;

public class ReadListTests
{
    private static readonly byte[] JsonValue = "\"text\""u8.ToArray();
    private static readonly byte[] FastValue = [0x10, (byte)'o', (byte)'k'];

    private readonly PassthroughValueSerializer _fast = new(0x10);

    public static TheoryData<string, string[]?, bool, bool> Matrix() => new()
    {
        { "null reads json", null, true, true },
        { "null reads fast", null, false, true },
        { "json only reads json", ["json"], true, true },
        { "json only misses fast", ["json"], false, false },
        { "fast only misses json", ["fast"], true, false },
        { "fast only reads fast", ["fast"], false, true },
        { "both read json", ["json", "fast"], true, true },
        { "both read fast", ["json", "fast"], false, true },
        { "names match ignoring case", ["JSON"], true, true },
    };

    [Fact]
    public void Format_outside_read_list_is_a_counted_miss_that_keeps_the_key()
    {
        var readable = Readable(["json"]);

        var outcome = Reader().Read<string>(FastValue, readable);

        outcome.IsMiss.Should().BeTrue();
        outcome.ExcludedFormat.Should().Be(0x10);
        outcome.IsUnreadable.Should().BeFalse("a value another policy or node reads is not removed");
        outcome.UnreadableFormat.Should().Be(0);
        _fast.Deserialized.Should().Be(0);
    }

    [Fact]
    public void Json_outside_read_list_is_a_counted_miss_that_keeps_the_key()
    {
        var readable = Readable(["fast"], write: _fast);

        var outcome = Reader().Read<string>(JsonValue, readable);

        outcome.IsMiss.Should().BeTrue();
        outcome.IsUnreadable.Should().BeFalse();
        outcome.ExcludedFormat.Should().Be(ReadableFormats.JsonId);
    }

    [Fact]
    public void An_id_nobody_registered_is_still_unreadable_and_removable_whatever_the_list_says()
    {
        var sut = Reader();

        sut.Read<string>([0x11, 1], Readable(["json"])).UnreadableFormat.Should().Be(0x11);
        sut.Read<string>([0x1F], Readable(["json"])).UnreadableFormat.Should().Be(-1);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Each_stored_format_is_read_or_a_miss_by_the_list(string why, string[]? read, bool storedJson, bool hit)
    {
        var readable = read is null ? ReadableFormats.Any : Readable(read, write: read.Contains("fast", StringComparer.OrdinalIgnoreCase) ? _fast : null);

        var outcome = Reader().Read<string>(storedJson ? JsonValue : FastValue, readable);

        outcome.IsMiss.Should().Be(!hit, why);
        if (hit)
        {
            outcome.Value.Should().Be(storedJson ? "text" : "ok");
        }
    }

    [Fact]
    public void Null_read_list_accepts_every_registered_format()
    {
        var other = new PassthroughValueSerializer(0x12);
        var sut = new ValueReader(new SystemJsonByteSerializerProxy(), new ValueSerializerRegistry([("fast", _fast, false), ("other", other, false)]));

        sut.Read<string>(FastValue, ReadableFormats.Any).Value.Should().Be("ok");
        sut.Read<string>([0x12, (byte)'o', (byte)'k'], ReadableFormats.Any).Value.Should().Be("ok");
        sut.Read<string>(JsonValue, ReadableFormats.Any).Value.Should().Be("text");
        Resolver().For(new CachePolicy(), forWrite: false).Readable.Should().BeSameAs(ReadableFormats.Any);
    }

    [Fact]
    public void An_unregistered_id_is_unreadable_with_no_list()
    {
        var outcome = Reader().Read<string>([0x11, 1], ReadableFormats.Any);

        outcome.UnreadableFormat.Should().Be(0x11);
        outcome.ExcludedFormat.Should().Be(0);
    }

    [Fact]
    public void Write_format_is_always_readable()
    {
        var sut = Resolver(defaultPolicy: new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } });
        var readsJsonOnly = new CachePolicy { Serializer = new SerializerPolicy { Read = ["json"] } };

        var format = sut.For(readsJsonOnly, forWrite: false);

        format.Writer.Should().BeNull();
        format.Readable!.Accepts(0x10).Should().BeTrue();
        format.Readable.Json.Should().BeTrue();
        Reader().Read<string>(FastValue, format.Readable).Value.Should().Be("ok");
    }

    [Fact]
    public void Write_format_is_readable_when_the_policy_writes_a_serializer_its_read_list_leaves_out()
    {
        var sut = Resolver();
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = ["json"] } };

        sut.For(policy, forWrite: true).Readable!.Accepts(0x10).Should().BeTrue();
    }

    [Fact]
    public void A_read_list_without_json_still_reads_json_while_the_node_list_can_turn_the_write_to_json()
    {
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = ["fast"] } };

        Resolver(effective: new Denies()).For(policy, forWrite: false).Readable!.Json.Should().BeTrue();
        Resolver().For(policy, forWrite: false).Readable!.Json.Should().BeFalse();
    }

    [Fact]
    public void A_node_list_that_allows_the_write_now_still_leaves_json_readable_for_when_it_stops()
    {
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = ["fast"] } };

        Resolver(effective: new Permits()).For(policy, forWrite: false).Readable!.Json.Should().BeTrue();
    }

    [Fact]
    public void A_node_list_that_changes_its_mind_between_calls_does_not_change_what_is_readable()
    {
        var gate = new Flips();
        var sut = Resolver(effective: gate);
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = ["fast"] } };

        var whileAllowed = sut.For(policy, forWrite: true);
        gate.Allowed = false;
        var afterwards = sut.For(policy, forWrite: true);

        whileAllowed.Writer.Should().BeSameAs(_fast);
        afterwards.Writer.Should().BeNull("the node list now holds the format back");
        whileAllowed.Readable!.Json.Should().BeTrue();
        afterwards.Readable!.Json.Should().BeTrue();
        Reader().Read<string>(JsonValue, afterwards.Readable).IsMiss.Should().BeFalse("JSON the node itself wrote while the node list held the format back reads");
    }

    [Fact]
    public void A_custom_proxy_has_no_json_to_fall_back_to_so_a_list_without_json_stays_without_it()
    {
        var sut = new ValueFormatResolver(new RawByteSerializerProxy(), Registry(), new CachePolicy(), new Denies());
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = ["fast"] } };

        sut.For(policy, forWrite: false).Readable!.Json.Should().BeFalse();
    }

    [Fact]
    public void A_policy_without_a_read_list_takes_the_default_policys()
    {
        var sut = Resolver(defaultPolicy: new CachePolicy { Serializer = new SerializerPolicy { Write = "json", Read = ["json"] } });

        var format = sut.For(new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(1) }, forWrite: false);

        format.Readable!.Accepts(0x10).Should().BeFalse();
        format.Readable.Json.Should().BeTrue();
    }

    [Fact]
    public void A_policys_own_read_list_wins_over_the_default_policys()
    {
        var sut = Resolver(defaultPolicy: new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = ["json", "fast"] } });
        var own = new CachePolicy { Serializer = new SerializerPolicy { Write = "json", Read = ["json"] } };

        var format = sut.For(own, forWrite: false);

        format.Readable!.Accepts(0x10).Should().BeFalse();
    }

    [Fact]
    public void A_read_list_that_names_an_unregistered_serializer_throws_from_a_call_site_policy()
    {
        var sut = Resolver();
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Read = ["json", "json2"] } };

        var act = () => sut.For(policy, forWrite: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*json2*");
    }

    [Fact]
    public void A_policy_with_a_read_list_resolves_without_allocating_once_seen()
    {
        var sut = Resolver();
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = ["json", "fast"] } };
        _ = sut.For(policy, forWrite: false);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = sut.For(policy, forWrite: false);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public void A_call_with_no_policy_or_a_new_policy_each_time_resolves_the_defaults_list_without_allocating()
    {
        var sut = Resolver(defaultPolicy: new CachePolicy { Serializer = new SerializerPolicy { Write = "json", Read = ["json", "fast"] } });
        var perCall = Enumerable.Range(0, 100).Select(_ => new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(1) }).ToArray();
        _ = sut.For(null, forWrite: false);
        _ = sut.For(perCall[0], forWrite: false);
        _ = sut.For(perCall[0], forWrite: true);

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var policy in perCall)
        {
            _ = sut.For(null, forWrite: false);
            _ = sut.For(policy, forWrite: true);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        sut.For(null, forWrite: false).Readable.Should().BeSameAs(sut.For(perCall[1], forWrite: false).Readable);
    }

    [Fact]
    public void A_new_policy_with_its_own_list_each_call_resolves_to_the_same_set_without_allocating()
    {
        var sut = Resolver();
        var perCall = Enumerable.Range(0, 100)
            .Select(_ => new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = ["json", "FAST"] } })
            .ToArray();
        var first = sut.For(perCall[0], forWrite: false).Readable;

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var policy in perCall)
        {
            _ = sut.For(policy, forWrite: false);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        sut.For(perCall[99], forWrite: false).Readable.Should().BeSameAs(first);
    }

    [Fact]
    public void A_policy_with_the_defaults_list_and_its_own_write_is_not_served_the_defaults_set()
    {
        var other = new PassthroughValueSerializer(0x12);
        var registry = new ValueSerializerRegistry([("fast", _fast, false), ("other", other, false)]);
        var sut = new ValueFormatResolver(new SystemJsonByteSerializerProxy(), registry, new CachePolicy { Serializer = new SerializerPolicy { Write = "json", Read = ["json", "fast"] } });
        var writesOther = new CachePolicy { Serializer = new SerializerPolicy { Write = "other" } };

        var format = sut.For(writesOther, forWrite: false);

        format.Readable!.Accepts(0x12).Should().BeTrue("a policy always reads what it writes");
        sut.For(null, forWrite: false).Readable!.Accepts(0x12).Should().BeFalse();
        sut.For(new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } }, forWrite: false).Readable!.Accepts(0x12).Should().BeFalse("the same list with another writer is another set");
    }

    [Fact]
    public void A_list_edited_after_it_was_first_read_is_read_again()
    {
        var sut = Resolver();
        string[] read = ["json", "fast"];
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "json", Read = read } };
        sut.For(policy, forWrite: false).Readable!.Accepts(0x10).Should().BeTrue();

        read[1] = "json";

        sut.For(policy, forWrite: false).Readable!.Accepts(0x10).Should().BeFalse();
    }

    [Fact]
    public void Unregistered_name_in_read_list_is_rejected()
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = new() { Serializer = new SerializerPolicy { Read = ["json", "json2"] } } };

        var act = () => new DefaultCachePolicyFactory(policies, null, default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*").WithMessage("*json2*");
    }

    [Theory]
    [InlineData("default")]
    [InlineData("orders")]
    public void A_read_list_without_json_fails_in_auto_mode_and_names_the_policy_and_the_fix(string scope)
    {
        var policy = FastOnly();
        var act = () => scope == "default"
            ? new DefaultCachePolicyFactory(null, policy, default, Registry(), FormatNegotiationMode.Auto)
            : new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { [scope] = policy }, null, default, Registry(), FormatNegotiationMode.Auto);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage($"*'{scope}'*")
            .WithMessage("*Serializer.Read*")
            .WithMessage("*\"json\"*")
            .WithMessage("*FormatNegotiation.Mode*Force*");
    }

    [Fact]
    public void Auto_is_the_default_mode()
    {
        var act = () => new DefaultCachePolicyFactory(null, FastOnly(), default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*Force*");
    }

    [Fact]
    public void A_read_list_without_json_is_valid_in_force_mode()
    {
        var sut = new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { ["orders"] = FastOnly() }, null, default, Registry(), FormatNegotiationMode.Force);

        CachePolicyMerger.WriteSerializerOf(sut.Resolve("orders")!).Should().BeSameAs(_fast);
    }

    [Theory]
    [InlineData(FormatNegotiationMode.Auto)]
    [InlineData(FormatNegotiationMode.Force)]
    public void A_read_list_with_json_is_valid_in_both_modes(FormatNegotiationMode mode)
    {
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = ["json", "fast"] } };

        var act = () => new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { ["orders"] = policy }, null, default, Registry(), mode);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(FormatNegotiationMode.Auto)]
    [InlineData(FormatNegotiationMode.Force)]
    public void A_null_read_list_is_valid_in_both_modes(FormatNegotiationMode mode)
    {
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } };

        var act = () => new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { ["orders"] = policy }, null, default, Registry(), mode);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(FormatNegotiationMode.Auto)]
    [InlineData(FormatNegotiationMode.Force)]
    public void An_empty_read_list_fails_in_both_modes(FormatNegotiationMode mode)
    {
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Read = [] } };

        var act = () => new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { ["orders"] = policy }, null, default, Registry(), mode);

        act.Should().Throw<OptionsValidationException>().WithMessage("*empty*");
    }

    [Theory]
    [InlineData(FormatNegotiationMode.Auto)]
    [InlineData(FormatNegotiationMode.Force)]
    public void A_json_only_read_list_that_writes_json_is_valid_in_both_modes(FormatNegotiationMode mode)
    {
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "json", Read = ["json"] } };

        var act = () => new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { ["orders"] = policy }, null, default, Registry(), mode);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_json_writer_without_json_in_read_keeps_its_own_message_in_force_mode()
    {
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Read = ["fast"] } };

        var act = () => new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { ["orders"] = policy }, null, default, Registry(), FormatNegotiationMode.Force);

        act.Should().Throw<OptionsValidationException>().WithMessage("*writes 'json'*");
    }

    [Theory]
    [InlineData(FormatNegotiationMode.Auto, true)]
    [InlineData(FormatNegotiationMode.Force, false)]
    public void The_configured_negotiation_mode_decides_whether_the_policy_factory_accepts_a_read_list_without_json(FormatNegotiationMode mode, bool fails)
    {
        var services = new ServiceCollection();
        services.AddCaching(
            builder =>
            {
                builder.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1");
                builder.AddRedis(o => o.FormatNegotiation.Mode = mode);
                builder.AddValueSerializer("fast", _ => _fast);
            },
            options => options.Policies["orders"] = FastOnly());
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ICachePolicyFactory>();

        if (fails)
        {
            act.Should().Throw<OptionsValidationException>().WithMessage("*orders*");
        }
        else
        {
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void An_app_with_no_redis_connection_has_no_node_list_so_the_rule_does_not_apply()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            builder => builder.AddValueSerializer("fast", _ => _fast),
            options => options.Policies["orders"] = FastOnly());
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ICachePolicyFactory>();

        act.Should().NotThrow();
    }

    [Fact]
    public void An_app_on_a_custom_proxy_has_no_json_to_fall_back_to_so_the_rule_does_not_apply()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISerializerProxy<byte[]>, RawByteSerializerProxy>();
        services.AddCaching(
            builder =>
            {
                builder.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1");
                builder.AddRedis(_ => { });
                builder.AddValueSerializer("fast", _ => _fast);
            },
            options => options.Policies["orders"] = FastOnly());
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ICachePolicyFactory>();

        act.Should().NotThrow();
    }

    [Fact]
    public void An_app_with_a_connection_and_no_redis_options_is_in_auto_mode()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            builder =>
            {
                builder.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1");
                builder.AddValueSerializer("fast", _ => _fast);
            },
            options => options.Policies["orders"] = FastOnly());
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ICachePolicyFactory>();

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*");
    }

    private static CachePolicy FastOnly() => new() { Serializer = new SerializerPolicy { Write = "fast", Read = ["fast"] } };

    private ValueSerializerRegistry Registry() => new([("fast", _fast, false)]);

    private ReadableFormats Readable(string[] read, ICacheValueSerializer? write = null) =>
        ReadableFormats.Resolve(Registry(), read, write, jsonFallback: false);

    private ValueReader Reader() => new(new SystemJsonByteSerializerProxy(), Registry());

    private ValueFormatResolver Resolver(CachePolicy? defaultPolicy = null, IEffectiveFormat? effective = null) =>
        new(new SystemJsonByteSerializerProxy(), Registry(), defaultPolicy ?? new CachePolicy(), effective);

    private sealed class Denies : IEffectiveFormat
    {
        public bool Allows(ICacheValueSerializer writer) => false;
    }

    private sealed class Permits : IEffectiveFormat
    {
        public bool Allows(ICacheValueSerializer writer) => true;
    }

    private sealed class Flips : IEffectiveFormat
    {
        public bool Allowed { get; set; } = true;

        public bool Allows(ICacheValueSerializer writer) => Allowed;
    }
}
