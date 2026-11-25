using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The key move writes the in-place keys in the format the group writes now: each encode asks for it, for strings and for hashes.
[Collection("RedisIntegration")]
public sealed class KeyMoveInPlaceFormatTests(RedisFixture fixture)
{
    private static readonly ManualTimeProvider Clock = new(new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_string_codec_asks_for_the_policy_at_every_encode()
    {
        var cache = new RecordingCodecCache();
        var current = WriteFormats.Envelope;
        var codec = new LibraryKeyMoveCodec<Item>(cache, 1024, () => new WriteLease(current, null));

        codec.Encode(new Item("a"));
        current = WriteFormats.MessagePackPolicy;
        codec.Encode(new Item("b"));

        cache.Policies.Should().Equal(WriteFormats.Envelope, WriteFormats.MessagePackPolicy);
    }

    [Fact]
    public void A_codec_with_no_policy_writes_the_default_format()
    {
        var cache = new RecordingCodecCache();
        new LibraryKeyMoveCodec<Item>(cache, 1024).Encode(new Item("a"));

        cache.Policies.Should().ContainSingle().Which.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_hash_codec_asks_for_the_policy_at_every_encode_with_or_without_system_fields(bool systemFields)
    {
        var cache = new RecordingHashCodecCache();
        var current = WriteFormats.Envelope;
        var codec = new LibraryKeyMoveHashCodec<Item>(cache, 1024, 16, systemFields, () => new WriteLease(current, null));

        codec.EncodeFields(new Dictionary<string, Item?> { ["a"] = new Item("1") }, null);
        current = WriteFormats.MessagePackPolicy;
        codec.EncodeFields(new Dictionary<string, Item?> { ["a"] = new Item("2") }, null);

        cache.Policies.Should().Equal(WriteFormats.Envelope, WriteFormats.MessagePackPolicy);
    }

    [Fact]
    public void A_binder_gives_each_in_place_codec_the_policy_of_its_group_and_leaves_the_native_side_alone()
    {
        var inPlace = new RecordingCodecCache();
        var native = new RecordingCodecCache();
        var asked = new List<string>();
        var binder = new KeyMoveBinder(inPlace, native, [Cache("c", "g1")], inPlacePolicy: group =>
        {
            asked.Add(group);
            return new WriteLease(WriteFormats.MessagePackPolicy, null);
        });
        var binding = binder.Bind<Item>("c")!;

        binding.InPlace.Encode(new Item("a"));
        binding.Native.Encode(new Item("a"));

        asked.Should().Equal("g1");
        inPlace.Policies.Should().Equal(WriteFormats.MessagePackPolicy);
        native.Policies.Should().ContainSingle().Which.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_hash_binding_applies_the_group_policy_only_to_the_in_place_side(bool systemFields)
    {
        var clock = NewClock();
        var inPlace = new RecordingHashCodecCache();
        var native = new RecordingHashCodecCache();
        var groups = new List<string>();
        var cache = Cache("c", "g") with { Kind = KeyMoveKind.Hash };
        var switchAt = clock.GetUtcNow().AddSeconds(1);
        var binder = new KeyMoveBinder(
            new RecordingCodecCache(),
            new RecordingCodecCache(),
            [cache],
            inPlaceHash: inPlace,
            nativeHash: native,
            inPlaceKeepsSystemFields: systemFields,
            inPlacePolicy: group =>
            {
                groups.Add(group);
                return new WriteLease(clock.GetUtcNow() >= switchAt ? WriteFormats.MessagePackPolicy : WriteFormats.Envelope, null);
            });
        var binding = binder.BindHash<Item>("c")!;
        var fields = new Dictionary<string, Item?> { ["a"] = new Item("value") };

        clock.Advance(TimeSpan.FromSeconds(1));
        binding.InPlace.EncodeFields(fields, null);
        binding.Native.EncodeFields(fields, null);

        groups.Should().Equal("g");
        inPlace.Policies.Should().Equal(WriteFormats.MessagePackPolicy);
        native.Policies.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task An_unswitched_managed_group_is_never_counted_by_a_gate_pass()
    {
        var clock = NewClock();
        var (selector, pin, pins, store) = CoordinatedSelector(clock, switched: false);
        var cancellationToken = TestContext.Current.CancellationToken;
        var pass = Task.CompletedTask;

        clock.Advance(TimeSpan.FromSeconds(1));
        using var lease = selector.CurrentPolicy(pin, () =>
        {
            pass = pins.RefreshAsync(cancellationToken);
            pass.IsCompletedSuccessfully.Should().BeTrue();
        });
        await pass;
        await pins.RefreshAsync(cancellationToken);

        lease.Policy.Should().Be(WriteFormats.Envelope);
        pin.Writing.Should().BeFalse();
        pin.Wanted.Should().BeFalse();
        store.Log.Should().NotContain("commit:g");
    }

    [Fact]
    public async Task A_switched_managed_group_registers_demand_and_writes_messagepack_after_a_gate_pass()
    {
        var clock = NewClock();
        var (selector, pin, pins, _) = CoordinatedSelector(clock, switched: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        await pins.RefreshAsync(cancellationToken);

        using var envelope = selector.CurrentPolicy(pin);
        envelope.Policy.Should().Be(WriteFormats.Envelope);
        clock.Advance(TimeSpan.FromSeconds(1));
        await pins.RefreshAsync(cancellationToken);

        using var messagePack = selector.CurrentPolicy(pin);
        messagePack.Policy.Should().Be(WriteFormats.MessagePackPolicy);
        pin.Wanted.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_in_place_write_keeps_gate_coverage_until_it_completes_or_throws(bool fail)
    {
        var clock = NewClock();
        var (selector, pin, pins, _) = CoordinatedSelector(clock, switched: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        pins.Want(pin, true);
        await pins.RefreshAsync(cancellationToken);
        var codec = new LibraryKeyMoveCodec<Item>(new RecordingCodecCache(), 1024, () => selector.CurrentPolicy(pin));
        await using var kit = await KeyMoveKit.CreateAsync(fixture, clock: clock, inPlace: codec);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1");

        string? hold = null;
        var writing = false;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceSet)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                pins.Want(pin, false);
                await pins.RefreshAsync(cancellationToken);
                writing = pin.Writing;
                hold = pins.HoldFor(pin);
                if (fail)
                {
                    throw new InvalidOperationException("write interrupted");
                }
            }
        };

        if (fail)
        {
            var write = async () => await kit.Client.SetAsync("k", new Item("value"), null, cancellationToken);
            await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("write interrupted");
        }
        else
        {
            (await kit.Client.SetAsync("k", new Item("value"), null, cancellationToken)).Outcome.Should().Be(WriteOutcome.Committed);
        }

        writing.Should().BeTrue();
        hold.Should().BeNull();
        pin.Writing.Should().BeFalse();
        await pins.RefreshAsync(cancellationToken);
        pins.HoldFor(pin).Should().NotBeNull();
    }

    [Theory]
    [InlineData("json")]
    [InlineData(null)]
    [InlineData("bogus")]
    public void The_current_format_is_the_envelope_for_a_group_that_does_not_ask_for_messagepack(string? answer)
    {
        var (selector, pin) = Selector(_ => answer);

        using var envelope = selector.CurrentPolicy(pin);
        envelope.Policy.Should().Be(WriteFormats.Envelope);
        pin.Wanted.Should().BeFalse("asking for the current format changes nothing");
    }

    [Fact]
    public void The_current_format_is_the_envelope_for_a_setting_that_throws()
    {
        var (selector, pin) = Selector(_ => throw new InvalidOperationException("the setting cannot be read"));

        using var envelope = selector.CurrentPolicy(pin);
        envelope.Policy.Should().Be(WriteFormats.Envelope);
    }

    [Fact]
    public void The_current_format_is_the_envelope_for_a_group_that_asks_for_messagepack_but_has_not_switched()
    {
        var (selector, pin) = Selector(_ => "messagepack");

        using var envelope = selector.CurrentPolicy(pin);
        envelope.Policy.Should().Be(WriteFormats.Envelope);
        pin.Wanted.Should().BeFalse("asking for the current format does not ask for the switch");
        pin.Writing.Should().BeFalse();
    }

    private static (WriteFormatSelector Selector, GroupPin Pin) Selector(Func<string, string?> format)
    {
        var pins = new PinCoordinator(new RouterCore(Clock, _ => true, NullLogger.Instance), _ => true, new FakePinStore(), null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2), NullLogger.Instance);
        return (new WriteFormatSelector(format, pins, NullLogger.Instance), pins.GroupFor("g"));
    }

    private static ManualTimeProvider NewClock() => new(Clock.GetUtcNow());

    private static (WriteFormatSelector Selector, GroupPin Pin, PinCoordinator Pins, FakePinStore Store) CoordinatedSelector(ManualTimeProvider clock, bool switched)
    {
        var store = new FakePinStore();
        var advertisement = new RouterAdvertisement("test", "build", ["ap=app", "pin=g"], false);
        var field = "test" + RouterAdvertisement.FieldSuffix;
        store.Nodes.Add(new NodeEntry(field, advertisement.Encode(), TimeSpan.Zero));
        if (switched)
        {
            store.Set("g", SwitchRead.Switched);
        }

        var pins = new PinCoordinator(new RouterCore(clock, _ => true, NullLogger.Instance), _ => true, store, "app", TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2), NullLogger.Instance)
        {
            OwnField = field,
            WriteFormat = _ => "messagepack",
        };
        var pin = pins.GroupFor("g");
        pins.EvaluatePins();
        return (new WriteFormatSelector(_ => "messagepack", pins, NullLogger.Instance), pin, pins, store);
    }

    private static KeyMoveCache Cache(string name, string group) =>
        new(name, group, false, false, false, null, "l:*", k => "l:" + k, k => k[2..], "n:*", k => "n:" + k, k => k[2..]);

    private sealed class RecordingCodecCache : IByteCodecCache
    {
        public List<CachePolicy?> Policies { get; } = [];

        public bool CachesNulls => false;

        public byte[] Encode<T>(T value, CachePolicy? policy)
        {
            Policies.Add(policy);
            return [1];
        }

        public (SnapshotOutcome Outcome, T? Value) Decode<T>(byte[] stored, CachePolicy? policy, bool quiet = false) => (SnapshotOutcome.Absent, default);
    }

    private sealed class RecordingHashCodecCache : IByteHashCodecCache
    {
        public List<CachePolicy?> Policies { get; } = [];

        public bool CachesNulls => false;

        public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields<T>(IDictionary<string, T?> values, IDictionary<string, string?>? metadata, CachePolicy? policy)
        {
            Policies.Add(policy);
            return [];
        }

        public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeUserFields<T>(IDictionary<string, T?> values, CachePolicy? policy)
        {
            Policies.Add(policy);
            return [];
        }

        public HashDecode<T> DecodeFields<T>(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored, CachePolicy? policy, bool quiet = false) => throw new NotSupportedException();
    }
}
