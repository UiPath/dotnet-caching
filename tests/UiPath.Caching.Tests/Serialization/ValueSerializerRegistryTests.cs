using System.Buffers;
using UiPath.Caching.Config;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Tests.Serialization;

public class ValueSerializerRegistryTests
{
    [Fact]
    public void Json_name_resolves_to_the_default_path()
    {
        var sut = new ValueSerializerRegistry([("fast", Fake(0x10), false)]);

        sut.Resolve("json").Should().BeNull();
        sut.Resolve("JSON").Should().BeNull();
    }

    [Theory, InlineData(null), InlineData(""), InlineData(" ")]
    public void A_blank_name_is_not_resolved(string? name)
    {
        var sut = new ValueSerializerRegistry([("fast", Fake(0x10), false)]);

        sut.TryResolve(name, out _).Should().BeFalse();
        sut.Invoking(r => r.Resolve(name)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Registered_name_resolves_ignoring_case()
    {
        var serializer = Fake(0x10);
        var sut = new ValueSerializerRegistry([("fast", serializer, false)]);

        sut.Resolve("FAST").Should().BeSameAs(serializer);
    }

    [Fact]
    public void Unknown_name_is_not_resolved()
    {
        var sut = new ValueSerializerRegistry([("fast", Fake(0x10), false)]);

        sut.TryResolve("slow", out _).Should().BeFalse();
        sut.TryResolve("json", out var json).Should().BeTrue();
        json.Should().BeNull();
        sut.Invoking(r => r.Resolve("slow")).Should().Throw<ArgumentException>().WithMessage("*slow*");
    }

    [Fact]
    public void Json_is_a_reserved_name()
    {
        var act = () => new ValueSerializerRegistry([("json", Fake(0x10), false)]);

        act.Should().Throw<ArgumentException>().WithMessage("*json*");
    }

    [Fact]
    public void Duplicate_name_is_rejected()
    {
        var act = () => new ValueSerializerRegistry([("fast", Fake(0x10), false), ("FAST", Fake(0x11), false)]);

        act.Should().Throw<ArgumentException>().WithMessage("*FAST*");
    }

    [Fact]
    public void Duplicate_format_id_is_rejected()
    {
        var act = () => new ValueSerializerRegistry([("a", Fake(0x10), false), ("b", Fake(0x10), false)]);

        act.Should().Throw<OptionsValidationException>().WithMessage("*0x10*");
    }

    [Fact]
    public void Repeated_id_inside_one_serializer_is_rejected()
    {
        var act = () => new ValueSerializerRegistry([("a", Fake(0x10, 0x10), false)]);

        act.Should().Throw<OptionsValidationException>().WithMessage("*0x10*");
    }

    [Fact]
    public void A_serializer_without_ids_is_rejected()
    {
        var act = () => new ValueSerializerRegistry([("a", Fake(), false)]);

        act.Should().Throw<OptionsValidationException>().WithMessage("*a*");
    }

    [Theory, InlineData(0x09), InlineData(0x0A), InlineData(0x0D), InlineData(0x1F), InlineData(0x00), InlineData(0x20), InlineData(0x200), InlineData(-1)]
    public void Invalid_ids_are_rejected(int id)
    {
        var consumer = () => new ValueSerializerRegistry([("a", Fake(id), false)]);
        var library = () => new ValueSerializerRegistry([("a", Fake(id), true)]);

        consumer.Should().Throw<OptionsValidationException>();
        library.Should().Throw<OptionsValidationException>();
    }

    [Theory, InlineData(0x01), InlineData(0x02), InlineData(0x0F)]
    public void Library_ids_are_rejected_for_consumers(int id)
    {
        var act = () => new ValueSerializerRegistry([("a", Fake(id), false)]);

        act.Should().Throw<OptionsValidationException>();
    }

    [Theory, InlineData(0x01), InlineData(0x02), InlineData(0x0F)]
    public void Library_entries_may_use_library_ids(int id)
    {
        var serializer = Fake(id);
        var sut = new ValueSerializerRegistry([("a", serializer, true)]);

        sut.TryGetByFormat(id, out var found).Should().BeTrue();
        found.Should().BeSameAs(serializer);
    }

    [Theory, InlineData(0x10), InlineData(0x1E), InlineData(0x100), InlineData(0x1FF)]
    public void Consumer_ids_are_accepted(int id)
    {
        var sut = new ValueSerializerRegistry([("a", Fake(id), false)]);

        sut.TryGetByFormat(id, out _).Should().BeTrue();
    }

    [Theory, InlineData(0x10), InlineData(0x1E), InlineData(0x100)]
    public void Consumer_ids_are_rejected_for_library_entries(int id)
    {
        var act = () => new ValueSerializerRegistry([("a", Fake(id), true)]);

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void The_library_range_ends_below_the_consumer_range()
    {
        var library = () => new ValueSerializerRegistry([("a", Fake(0x10), true)]);
        var consumer = () => new ValueSerializerRegistry([("a", Fake(0x0F), false)]);

        library.Should().Throw<OptionsValidationException>();
        consumer.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void The_snapshot_of_ids_does_not_follow_the_serializer()
    {
        var ids = new List<int> { 0x10 };
        var serializer = new FakeSerializer(ids);
        var sut = new ValueSerializerRegistry([("a", serializer, false)]);

        ids.Add(0x11);

        sut.FormatIdsOf(serializer).ToArray().Should().Equal(0x10);
    }

    [Fact]
    public void Ids_of_a_serializer_from_elsewhere_are_refused()
    {
        var sut = new ValueSerializerRegistry([("a", Fake(0x10), false)]);

        sut.Invoking(r => r.FormatIdsOf(Fake(0x10))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Dispose_disposes_the_serializers_it_holds()
    {
        var owned = new DisposableSerializer(0x10);
        var sut = new ValueSerializerRegistry([("a", owned, false), ("b", Fake(0x11), false)]);

        sut.Dispose();

        owned.Disposed.Should().Be(1);
    }

    [Fact]
    public void Dispose_leaves_borrowed_serializers_alone()
    {
        var borrowed = new DisposableSerializer(0x10);
        var sut = new ValueSerializerRegistry([("a", borrowed, false)], [borrowed]);

        sut.Dispose();

        borrowed.Disposed.Should().Be(0);
    }

    [Fact]
    public void A_serializer_whose_ids_are_null_is_rejected_by_name()
    {
        var act = () => new ValueSerializerRegistry([("fast", new FakeSerializer(null!), false)]);

        act.Should().Throw<OptionsValidationException>().WithMessage("*fast*FormatIds*");
    }

    [Fact]
    public void Dispose_disposes_a_serializer_that_is_only_async_disposable()
    {
        var owned = new AsyncDisposableSerializer(0x10);
        var sut = new ValueSerializerRegistry([("a", owned, false)]);

        sut.Dispose();

        owned.Disposed.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_disposes_async_only_and_sync_only_serializers_but_not_borrowed_ones()
    {
        var asyncOnly = new AsyncDisposableSerializer(0x10);
        var syncOnly = new DisposableSerializer(0x11);
        var borrowed = new AsyncDisposableSerializer(0x12);
        var sut = new ValueSerializerRegistry([("a", asyncOnly, false), ("b", syncOnly, false), ("c", borrowed, false)], [borrowed]);

        await sut.DisposeAsync();

        asyncOnly.Disposed.Should().Be(1);
        syncOnly.Disposed.Should().Be(1);
        borrowed.Disposed.Should().Be(0);
    }

    [Fact]
    public void A_per_call_policy_naming_a_registered_serializer_resolves_it()
    {
        var serializer = Fake(0x10);
        var sut = new ValueSerializerRegistry([("fast", serializer, false)]);
        var factory = new DefaultCachePolicyFactory(null, new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) }, default, sut);
        var perCall = CachePolicyMerger.Merge(new CachePolicy { Serializer = new SerializerPolicy { Write = "FAST" } }, factory.Default)!;

        CachePolicyMerger.WriteSerializerOf(perCall).Should().BeNull();
        sut.WriteSerializerFor(perCall).Should().BeSameAs(serializer);
    }

    [Fact]
    public void A_per_call_policy_naming_an_unknown_serializer_throws()
    {
        var sut = new ValueSerializerRegistry([("fast", Fake(0x10), false)]);
        var perCall = new CachePolicy { Serializer = new SerializerPolicy { Write = "typo" } };

        sut.Invoking(r => r.WriteSerializerFor(perCall)).Should().Throw<OptionsValidationException>().WithMessage("*typo*Serializer.Write*");
    }

    [Fact]
    public void A_per_call_policy_overriding_a_resolved_default_resolves_its_own_name()
    {
        var fast = Fake(0x10);
        var other = Fake(0x11);
        var sut = new ValueSerializerRegistry([("fast", fast, false), ("other", other, false)]);
        var factory = new DefaultCachePolicyFactory(null, new CachePolicy { Serializer = new SerializerPolicy { Write = "fast" } }, default, sut);
        var perCall = CachePolicyMerger.Merge(new CachePolicy { Serializer = new SerializerPolicy { Write = "other" } }, factory.Default)!;

        sut.WriteSerializerFor(perCall).Should().BeSameAs(other);
        sut.WriteSerializerFor(factory.Default).Should().BeSameAs(fast);
    }

    [Theory, InlineData(null), InlineData("json"), InlineData("JSON")]
    public void Json_and_unset_write_names_keep_the_json_path(string? write)
    {
        var sut = new ValueSerializerRegistry([("fast", Fake(0x10), false)]);

        sut.WriteSerializerFor(new CachePolicy { Serializer = new SerializerPolicy { Write = write } }).Should().BeNull();
        sut.WriteSerializerFor(new CachePolicy()).Should().BeNull();
        sut.WriteSerializerFor(null).Should().BeNull();
    }

    [Fact]
    public void A_custom_factory_naming_an_unknown_serializer_is_rejected()
    {
        var sut = new ValueSerializerRegistry([("fast", Fake(0x10), false)]);
        var named = Substitute.For<ICachePolicyFactory>();
        named.Keys.Returns(["orders"]);
        named.Resolve("orders").Returns(new CachePolicy { Serializer = new SerializerPolicy { Write = "typo" } });
        var defaults = Substitute.For<ICachePolicyFactory>();
        defaults.Keys.Returns([]);
        defaults.Default.Returns(new CachePolicy { Serializer = new SerializerPolicy { Write = "typo" } });

        sut.Invoking(r => r.ValidateWriteNames(named)).Should().Throw<OptionsValidationException>().WithMessage("*orders*typo*");
        sut.Invoking(r => r.ValidateWriteNames(defaults)).Should().Throw<OptionsValidationException>().WithMessage("*default*typo*");
    }

    [Fact]
    public void Extended_ids_resolve_by_format()
    {
        var serializer = Fake(0x105);
        var sut = new ValueSerializerRegistry([("a", serializer, false)]);

        sut.TryGetByFormat(0x105, out var found).Should().BeTrue();
        found.Should().BeSameAs(serializer);
        sut.TryGetByFormat(0x106, out _).Should().BeFalse();
    }

    [Fact]
    public void A_serializer_owns_all_its_ids()
    {
        var serializer = Fake(0x10, 0x11);
        var sut = new ValueSerializerRegistry([("a", serializer, false)]);

        sut.TryGetByFormat(0x10, out var first).Should().BeTrue();
        sut.TryGetByFormat(0x11, out var second).Should().BeTrue();
        first.Should().BeSameAs(serializer);
        second.Should().BeSameAs(serializer);
    }

    [Fact]
    public void Mixed_header_lengths_are_rejected()
    {
        var act = () => new ValueSerializerRegistry([("a", Fake(0x10, 0x105), false)]);

        act.Should().Throw<OptionsValidationException>().WithMessage("*a*");
    }

    [Fact]
    public void Empty_registry_reports_empty()
    {
        new ValueSerializerRegistry([]).IsEmpty.Should().BeTrue();
        new ValueSerializerRegistry([("a", Fake(0x10), false)]).IsEmpty.Should().BeFalse();
    }

    private static FakeSerializer Fake(params int[] ids) => new(ids);

    private sealed class DisposableSerializer(params int[] ids) : FakeSerializer(ids), IDisposable
    {
        public int Disposed { get; private set; }

        public void Dispose() => Disposed++;
    }

    private sealed class AsyncDisposableSerializer(params int[] ids) : FakeSerializer(ids), IAsyncDisposable
    {
        public int Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    private class FakeSerializer(IReadOnlyList<int> ids) : ICacheValueSerializer
    {
        public IReadOnlyList<int> FormatIds { get; } = ids;

        public int Serialize<T>(T value, IBufferWriter<byte> target) => throw new NotSupportedException();

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => throw new NotSupportedException();
    }
}
