using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Config;

public class ValueSerializerRegistrationTests
{
    private static readonly Func<IServiceProvider, ICacheValueSerializer> Factory = _ => new FakeValueSerializer(0x10);

    [Theory]
    [InlineData(0x00), InlineData(0x09), InlineData(0x0A), InlineData(0x0D), InlineData(0x1F), InlineData(0x20), InlineData(0x200), InlineData(-1)]
    public void An_id_outside_the_allowed_ranges_is_refused(int id)
    {
        var (builder, services) = Create();
        var before = services.Count;

        var act = () => builder.AddValueSerializer("a", [id], Factory);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("formatIds").WithMessage($"*'a'*{FormatIdRules.Hex(id)}*");
        services.Count.Should().Be(before);
    }

    [Fact]
    public void An_empty_list_is_refused()
    {
        var (builder, services) = Create();
        var before = services.Count;

        var act = () => builder.AddValueSerializer("a", [], Factory);

        act.Should().Throw<ArgumentException>().WithParameterName("formatIds").WithMessage("*'a' declares no format ids*");
        services.Count.Should().Be(before);
    }

    [Fact]
    public void An_id_listed_twice_is_refused()
    {
        var (builder, services) = Create();
        var before = services.Count;

        var act = () => builder.AddValueSerializer("a", [0x10, 0x11, 0x10], Factory);

        act.Should().Throw<ArgumentException>().WithParameterName("formatIds").WithMessage("*0x10 is listed twice*'a'*");
        services.Count.Should().Be(before);
    }

    [Theory]
    [InlineData(0x10, 0x105), InlineData(0x105, 0x10)]
    public void Mixed_header_lengths_are_refused(int first, int second)
    {
        var (builder, services) = Create();
        var before = services.Count;

        var act = () => builder.AddValueSerializer("a", [first, second], Factory);

        act.Should().Throw<ArgumentException>().WithParameterName("formatIds").WithMessage("*mixes one-byte and extended*");
        services.Count.Should().Be(before);
    }

    [Fact]
    public void An_id_another_serializer_claimed_is_refused_and_the_first_stays()
    {
        var (builder, services) = Create();
        builder.AddValueSerializer("a", [0x10, 0x11], Factory);
        var before = services.Count;

        var act = () => builder.AddValueSerializer("b", [0x12, 0x11], Factory);

        act.Should().Throw<ArgumentException>().WithParameterName("formatIds").WithMessage("*0x11 is already claimed by value serializer 'a'; 'b' cannot claim it*");
        services.Count.Should().Be(before);
        Registrations(services).Select(r => r.Name).Should().Equal("a");
    }

    [Fact]
    public void A_name_used_twice_in_another_case_is_refused()
    {
        var (builder, services) = Create();
        builder.AddValueSerializer("a", [0x10], Factory);
        var before = services.Count;

        var act = () => builder.AddValueSerializer("A", [0x11], Factory);

        act.Should().Throw<ArgumentException>().WithParameterName("name").WithMessage("*'A' is already registered*");
        services.Count.Should().Be(before);
    }

    [Theory]
    [InlineData("json"), InlineData("JSON"), InlineData(""), InlineData(" ")]
    public void A_reserved_or_blank_name_is_refused(string name)
    {
        var (builder, services) = Create();
        var before = services.Count;

        var act = () => builder.AddValueSerializer(name, [0x10], Factory);

        act.Should().Throw<ArgumentException>().WithParameterName(nameof(name));
        services.Count.Should().Be(before);
    }

    [Fact]
    public void Missing_arguments_are_refused()
    {
        var (builder, services) = Create();
        var before = services.Count;

        builder.Invoking(b => b.AddValueSerializer("a", null!, Factory)).Should().Throw<ArgumentNullException>().WithParameterName("formatIds");
        builder.Invoking(b => b.AddValueSerializer("a", [0x10], null!)).Should().Throw<ArgumentNullException>().WithParameterName("factory");
        var nullBuilder = () => ((ICachingBuilder)null!).AddValueSerializer("a", [0x10], Factory);
        nullBuilder.Should().Throw<ArgumentNullException>().WithParameterName("builder");
        services.Count.Should().Be(before);
    }

    [Fact]
    public void The_declared_ids_are_copied_when_the_serializer_is_added()
    {
        var (builder, services) = Create();
        var ids = new List<int> { 0x10 };

        builder.AddValueSerializer("a", ids, Factory);
        ids[0] = 0x11;
        ids.Add(0x12);

        Registrations(services).Single().Ids.Should().Equal(0x10);
    }

    [Fact]
    public void A_forwarded_serializer_of_the_same_name_is_replaced_without_conflict()
    {
        var (builder, services) = Create();
        services.AddSingleton(new ValueSerializerRegistration("a", [0x10], Factory, Forwarded: true));

        builder.AddValueSerializer("a", [0x10, 0x11], Factory);

        Registrations(services).Select(r => (r.Name, r.Forwarded)).Should().Equal(("a", true), ("a", false));
    }

    [Fact]
    public void A_forwarded_id_cannot_be_claimed_under_another_name()
    {
        var (builder, services) = Create();
        services.AddSingleton(new ValueSerializerRegistration("a", [0x10], Factory, Forwarded: true));
        var before = services.Count;

        var act = () => builder.AddValueSerializer("b", [0x10], Factory);

        act.Should().Throw<ArgumentException>().WithParameterName("formatIds").WithMessage("*0x10 is already claimed by value serializer 'a'*");
        services.Count.Should().Be(before);
    }

    [Fact]
    public void An_id_a_replaced_forwarded_serializer_held_can_be_claimed()
    {
        var (builder, services) = Create();
        services.AddSingleton(new ValueSerializerRegistration("a", [0x10], Factory, Forwarded: true));
        builder.AddValueSerializer("a", [0x11], Factory);

        builder.AddValueSerializer("b", [0x10], Factory);

        Registrations(services).Where(r => !r.Forwarded).Select(r => r.Name).Should().Equal("a", "b");
    }

    [Fact]
    public void An_id_a_forwarded_serializer_holds_is_refused_until_a_stack_serializer_replaces_it()
    {
        var (builder, services) = Create();
        services.AddSingleton(new ValueSerializerRegistration("a", [0x10], Factory, Forwarded: true));

        var before = builder.Invoking(b => b.AddValueSerializer("b", [0x10], Factory));
        before.Should().Throw<ArgumentException>().WithMessage("*'a'*");
        builder.AddValueSerializer("a", [0x11], Factory);
        builder.AddValueSerializer("b", [0x10], Factory);

        Registrations(services).Where(r => !r.Forwarded).Select(r => r.Name).Should().Equal("a", "b");
    }

    [Fact]
    public void A_serializer_that_reports_an_id_twice_fails_the_build()
    {
        var serializer = new DisposableFakeValueSerializer(0x10, 0x10);
        var (builder, services) = Create();
        builder.AddValueSerializer("a", [0x10], _ => serializer);
        builder.Complete();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ValueSerializerRegistry>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*'a'*");
        serializer.Disposed.Should().Be(1);
    }

    [Theory]
    [InlineData(new[] { 0x10 }, new[] { 0x10, 0x11 })]
    [InlineData(new[] { 0x10, 0x11 }, new[] { 0x10 })]
    [InlineData(new[] { 0x10, 0x11 }, new[] { 0x10, 0x12 })]
    [InlineData(new[] { 0x10 }, new int[0])]
    public void Ids_the_serializer_reports_must_match_the_declared_ones_and_a_failed_build_disposes_it(int[] declared, int[] reported)
    {
        var serializer = new DisposableFakeValueSerializer(reported);
        var (builder, services) = Create();
        builder.AddValueSerializer("a", declared, _ => serializer);
        builder.Complete();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ValueSerializerRegistry>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*'a' was registered with format ids*but its serializer reports*Declare exactly the ids it reports*");
        serializer.Disposed.Should().Be(1);
    }

    [Fact]
    public void The_message_lists_both_sets()
    {
        var (builder, services) = Create();
        builder.AddValueSerializer("x", [0x10, 0x11], _ => new FakeValueSerializer(0x10));
        builder.Complete();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ValueSerializerRegistry>();

        act.Should().Throw<InvalidOperationException>().WithMessage("Value serializer 'x' was registered with format ids 0x10, 0x11 but its serializer reports 0x10. Declare exactly the ids it reports.");
    }

    [Fact]
    public void A_serializer_that_reports_null_ids_fails_the_build()
    {
        var serializer = new NullIdsSerializer();
        var (builder, services) = Create();
        builder.AddValueSerializer("a", [0x10], _ => serializer);
        builder.Complete();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ValueSerializerRegistry>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*'a'*reports none*");
    }

    [Fact]
    public void A_later_mismatch_disposes_the_serializers_built_before_it()
    {
        var good = new DisposableFakeValueSerializer(0x10);
        var bad = new DisposableFakeValueSerializer(0x12);
        var (builder, services) = Create();
        builder.AddValueSerializer("a", [0x10], _ => good);
        builder.AddValueSerializer("b", [0x11], _ => bad);
        builder.Complete();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ValueSerializerRegistry>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*'b'*");
        good.Disposed.Should().Be(1);
        bad.Disposed.Should().Be(1);
    }

    [Fact]
    public void The_same_ids_in_another_order_are_accepted_and_the_declared_order_is_kept()
    {
        var serializer = new FakeValueSerializer(0x11, 0x10);
        var (builder, services) = Create();
        builder.AddValueSerializer("a", [0x10, 0x11], _ => serializer);
        builder.Complete();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ValueSerializerRegistry>();

        registry.FormatIdsOf(serializer).ToArray().Should().Equal(0x10, 0x11);
        registry.NameOf(serializer).Should().Be("a");
    }

    private static (CachingBuilder Builder, ServiceCollection Services) Create()
    {
        var services = new ServiceCollection();
        return (new CachingBuilder(services), services);
    }

    private static List<ValueSerializerRegistration> Registrations(IServiceCollection services) =>
        services.Select(d => d.ImplementationInstance).OfType<ValueSerializerRegistration>().ToList();

    private sealed class NullIdsSerializer : ICacheValueSerializer
    {
        public IReadOnlyList<int> FormatIds => null!;

        public int Serialize<T>(T value, IBufferWriter<byte> target) => throw new NotSupportedException();

        public T? Deserialize<T>(int formatId, ReadOnlySequence<byte> source) => throw new NotSupportedException();
    }
}
