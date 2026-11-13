using UiPath.Caching.Config;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Serialization;

public class ValueMigrationTests
{
    private static readonly byte[] Json = "\"x\""u8.ToArray();
    private static readonly byte[] Fast = [0x10, (byte)'x'];
    private static readonly byte[] Other = [0x12, (byte)'x'];
    private static readonly byte[] Unknown = [0x14, (byte)'x'];

    private readonly PassthroughValueSerializer _fast = new(0x10, 0x11);
    private readonly PassthroughValueSerializer _other = new(0x12);

    [Fact]
    public void A_policy_that_migrates_names_its_write_serializer_as_the_target()
    {
        var sut = Resolver();

        sut.For(Policy("fast", migrate: true), forWrite: false).Readable!.MigrateTo.Should().BeSameAs(_fast);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void A_policy_that_does_not_migrate_has_no_target_and_the_set_it_had_before(bool? migrate)
    {
        var sut = Resolver();

        var readable = sut.For(Policy("fast", migrate, read: ["json", "fast"]), forWrite: false).Readable!;
        var again = sut.For(Policy("fast", migrate, read: ["json", "fast"]), forWrite: false).Readable!;

        readable.MigrateTo.Should().BeNull();
        again.Should().BeSameAs(readable);
        sut.For(Policy("fast", migrate: null), forWrite: false).Readable.Should().BeSameAs(ReadableFormats.Any);
    }

    [Fact]
    public void A_policy_that_writes_json_never_migrates_toward_it()
    {
        var sut = Resolver();

        sut.For(Policy("json", migrate: true), forWrite: false).Readable!.MigrateTo.Should().BeNull();
        sut.For(Policy("json", migrate: true, read: ["json", "fast"]), forWrite: false).Readable!.MigrateTo.Should().BeNull();
    }

    [Fact]
    public void A_policy_with_no_read_list_still_migrates_and_still_accepts_every_format()
    {
        var sut = Resolver();

        var readable = sut.For(Policy("fast", migrate: true, read: null), forWrite: false).Readable!;

        readable.MigrateTo.Should().BeSameAs(_fast);
        readable.Accepts(0x12).Should().BeTrue();
        readable.Json.Should().BeTrue();
        sut.For(Policy("fast", migrate: true, read: null), forWrite: false).Readable.Should().BeSameAs(readable);
    }

    [Fact]
    public void A_policy_with_a_read_list_migrates_within_it()
    {
        var sut = Resolver();

        var readable = sut.For(Policy("fast", migrate: true, read: ["json", "fast"]), forWrite: false).Readable!;

        readable.MigrateTo.Should().BeSameAs(_fast);
        readable.Accepts(0x12).Should().BeFalse();
        readable.Json.Should().BeTrue();
    }

    [Fact]
    public void A_policy_that_sets_nothing_takes_the_default_policys_migration_and_one_that_sets_it_wins()
    {
        var sut = Resolver(defaultPolicy: Policy("fast", migrate: true, read: ["json", "fast"]));

        sut.For(new CachePolicy(), forWrite: false).Readable!.MigrateTo.Should().BeSameAs(_fast);
        sut.For(policy: null, forWrite: false).Readable!.MigrateTo.Should().BeSameAs(_fast);
        sut.For(new CachePolicy { Serializer = new SerializerPolicy { MigrateOnRead = false } }, forWrite: false).Readable!.MigrateTo.Should().BeNull();
        sut.For(policy: null, forWrite: false).Readable.Should().BeSameAs(sut.For(new CachePolicy(), forWrite: false).Readable, "the default's set is built once");
    }

    [Fact]
    public void A_policy_that_migrates_while_the_default_does_not_gets_its_own_set()
    {
        var sut = Resolver(defaultPolicy: Policy("fast", migrate: null, read: ["json", "fast"]));

        var plain = sut.For(policy: null, forWrite: false).Readable!;
        var migrating = sut.For(new CachePolicy { Serializer = new SerializerPolicy { MigrateOnRead = true } }, forWrite: false).Readable!;

        plain.MigrateTo.Should().BeNull();
        migrating.MigrateTo.Should().BeSameAs(_fast);
        sut.For(policy: null, forWrite: false).Readable.Should().BeSameAs(plain);
    }

    [Fact]
    public void A_policy_that_migrates_and_one_that_does_not_never_share_a_set_though_they_list_the_same_formats()
    {
        var sut = Resolver();

        var migrating = sut.For(Policy("fast", migrate: true, read: ["json", "fast"]), forWrite: false).Readable!;
        var plain = sut.For(Policy("fast", migrate: false, read: ["json", "fast"]), forWrite: false).Readable!;
        var migratingAgain = sut.For(Policy("fast", migrate: true, read: ["json", "fast"]), forWrite: false).Readable!;

        migrating.MigrateTo.Should().BeSameAs(_fast);
        plain.MigrateTo.Should().BeNull();
        migratingAgain.Should().BeSameAs(migrating);
    }

    [Fact]
    public void A_value_over_the_size_a_rewrite_may_carry_is_not_wanted()
    {
        var sut = Migration();

        sut.Wants(_fast, new byte[ValueMigration.MaxValueBytes]).Should().BeTrue("a value of exactly the most is still offered");
        sut.Wants(_fast, new byte[ValueMigration.MaxValueBytes + 1]).Should().BeFalse();
    }

    [Fact]
    public void The_target_is_the_configured_format_whatever_the_node_list_allows_now()
    {
        var sut = Resolver(effective: new Denies());

        sut.For(Policy("fast", migrate: true), forWrite: false).Readable!.MigrateTo.Should().BeSameAs(_fast);
    }

    [Fact]
    public void A_target_that_may_be_written_wants_a_json_value_and_one_in_another_registered_format()
    {
        var sut = Migration();

        sut.Wants(_fast, Json).Should().BeTrue();
        sut.Wants(_fast, Other).Should().BeTrue();
        sut.Wants(_other, Fast).Should().BeTrue();
    }

    [Fact]
    public void A_value_already_in_a_format_the_target_owns_is_not_wanted()
    {
        var sut = Migration();

        sut.Wants(_fast, Fast).Should().BeFalse();
        sut.Wants(_fast, [0x11, (byte)'x']).Should().BeFalse("the target's other id is the same serializer's, which picks it by size");
    }

    [Fact]
    public void Json_a_custom_proxy_wrote_is_not_wanted_since_it_could_only_be_looked_at_and_left()
    {
        var registry = Registry();
        var migrator = new FormatMigrator(new RecordingTelemetryProvider(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var sut = new ValueMigration(migrator, new ValueRewriter(new SubclassedProxy(), registry), registry, effective: null);

        sut.Wants(_fast, Json).Should().BeFalse();
        sut.Wants(_fast, Other).Should().BeTrue("a headered value does not depend on the proxy");
    }

    [Fact]
    public void An_id_nobody_owns_an_empty_payload_and_a_cut_header_are_not_wanted()
    {
        var sut = Migration();

        sut.Wants(_fast, Unknown).Should().BeFalse();
        sut.Wants(_fast, []).Should().BeFalse();
        sut.Wants(_fast, [0x1F]).Should().BeFalse();
    }

    [Fact]
    public void Nothing_is_wanted_while_the_node_list_holds_the_format_back()
    {
        var gate = new Flips { Allowed = false };
        var sut = Migration(gate);

        sut.Wants(_fast, Json).Should().BeFalse();
        sut.Wants(_fast, Other).Should().BeFalse();

        gate.Allowed = true;

        sut.Wants(_fast, Json).Should().BeTrue();
        sut.Wants(_fast, Other).Should().BeTrue();
    }

    [Fact]
    public void With_no_node_list_to_ask_the_configured_format_is_the_effective_one()
    {
        var sut = Migration(effective: null);

        sut.Wants(_fast, Json).Should().BeTrue();
    }

    [Fact]
    public void A_target_serializer_that_throws_for_the_value_is_a_value_that_would_not_come_out_the_same_and_is_logged()
    {
        var target = new ThrowingSerializer();
        var registry = new ValueSerializerRegistry([("fast", target, false)]);
        var logger = new RecordedLogger();
        using var sut = new ValueMigration(
            new FormatMigrator(new RecordingTelemetryProvider(), TimeProvider.System, logger),
            new ValueRewriter(new SystemJsonByteSerializerProxy(), registry),
            registry,
            effective: null);

        sut.TryReencode<string>(Json, target, out var payload).Should().BeFalse();

        payload.Memory.IsEmpty.Should().BeTrue();
        logger.Records.Should().ContainSingle(r => r.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    private static CachePolicy Policy(string write, bool? migrate, string[]? read = null) =>
        new() { Serializer = new SerializerPolicy { Write = write, Read = read, MigrateOnRead = migrate } };

    private ValueSerializerRegistry Registry() => new([("fast", _fast, false), ("other", _other, false)]);

    private ValueFormatResolver Resolver(CachePolicy? defaultPolicy = null, IEffectiveFormat? effective = null) =>
        new(new SystemJsonByteSerializerProxy(), Registry(), defaultPolicy ?? new CachePolicy(), effective);

    private ValueMigration Migration(IEffectiveFormat? effective = null)
    {
        var registry = Registry();
        var migrator = new FormatMigrator(new RecordingTelemetryProvider(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return new(migrator, new ValueRewriter(new SystemJsonByteSerializerProxy(), registry), registry, effective);
    }

    private sealed class ThrowingSerializer : ICacheValueSerializer
    {
        public IReadOnlyList<int> FormatIds { get; } = [0x10];

        public int Serialize<T>(T value, System.Buffers.IBufferWriter<byte> target) => throw new NotSupportedException("cannot carry the type");

        public T? Deserialize<T>(int formatId, System.Buffers.ReadOnlySequence<byte> source) => throw new NotSupportedException();
    }

    private sealed class SubclassedProxy : SystemJsonByteSerializerProxy
    {
    }

    private sealed class Denies : IEffectiveFormat
    {
        public bool Allows(ICacheValueSerializer writer) => false;
    }

    private sealed class Flips : IEffectiveFormat
    {
        public bool Allowed { get; set; } = true;

        public bool Allows(ICacheValueSerializer writer) => Allowed;
    }
}
