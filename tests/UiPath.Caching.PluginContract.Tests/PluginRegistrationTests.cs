using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Config;
using UiPath.Caching.PluginContract.Tests.Acme;

namespace UiPath.Caching.PluginContract.Tests;

public class PluginRegistrationTests
{
    public static TheoryData<string?, int[]?, Type> RejectedValueSerializers() => new()
    {
        { "ACME", [0x110], typeof(ArgumentException) },
        { "json", [0x110], typeof(ArgumentException) },
        { " ", [0x110], typeof(ArgumentException) },
        { null, [0x110], typeof(ArgumentNullException) },
        { "other", null, typeof(ArgumentNullException) },
        { "other", [], typeof(ArgumentException) },
        { "other", [0x110, 0x110], typeof(ArgumentException) },
        { "other", [0x12, 0x110], typeof(ArgumentException) },
        { "other", [0x100], typeof(ArgumentException) },
        { "other", [0x101], typeof(ArgumentException) },
        { "other", [0x00], typeof(ArgumentOutOfRangeException) },
        { "other", [0x09], typeof(ArgumentOutOfRangeException) },
        { "other", [0x0A], typeof(ArgumentOutOfRangeException) },
        { "other", [0x0D], typeof(ArgumentOutOfRangeException) },
        { "other", [0x1F], typeof(ArgumentOutOfRangeException) },
        { "other", [0x20], typeof(ArgumentOutOfRangeException) },
        { "other", [0x200], typeof(ArgumentOutOfRangeException) },
    };

    public static TheoryData<string, int, bool, Type> RejectedEventFormats() => new()
    {
        { "other", AcmeFormats.Event, false, typeof(ArgumentException) },
        { "ACME", 0x12, false, typeof(ArgumentException) },
        { "json", 0x12, false, typeof(ArgumentException) },
        { "other", 0x00, false, typeof(ArgumentOutOfRangeException) },
        { "other", 0x1F, false, typeof(ArgumentOutOfRangeException) },
        { "other", 0x100, false, typeof(ArgumentOutOfRangeException) },
        { "other", 0x12, true, typeof(InvalidOperationException) },
    };

    [Fact]
    public void The_registration_calls_return_the_builder_so_a_plugin_can_chain_them()
    {
        var builder = new CachingBuilder(new ServiceCollection());

        var result = builder
            .AddValueSerializer(AcmeFormats.Name, AcmeFormats.ValueIds, _ => new AcmeValueSerializer())
            .AddEventFormat(AcmeFormats.Name, AcmeFormats.Event, _ => new AcmeEventFormat(), write: true);

        result.Should().BeSameAs(builder);
    }

    [Theory]
    [MemberData(nameof(RejectedValueSerializers))]
    public void A_rejected_value_serializer_throws_at_the_call_and_changes_nothing(string? name, int[]? ids, Type expected)
    {
        var builder = new CachingBuilder(new ServiceCollection());
        builder.AddValueSerializer(AcmeFormats.Name, AcmeFormats.ValueIds, _ => new AcmeValueSerializer());
        var before = Snapshot(builder);

        var act = () => builder.AddValueSerializer(name!, ids!, _ => new AcmeValueSerializer(ids: ids));

        act.Should().Throw<Exception>().Which.Should().BeOfType(expected);
        Snapshot(builder).Should().Equal(before, ReferenceEquals);
    }

    [Fact]
    public void A_missing_factory_or_builder_throws()
    {
        var builder = new CachingBuilder(new ServiceCollection());

        ((Action)(() => builder.AddValueSerializer("other", [0x110], null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => ((ICachingBuilder)null!).AddValueSerializer("other", [0x110], _ => new AcmeValueSerializer()))).Should().Throw<ArgumentNullException>();
        ((Action)(() => builder.AddEventFormat("other", 0x12, null!, write: false))).Should().Throw<ArgumentNullException>();
        builder.Services.Should().BeEmpty();
    }

    [Fact]
    public void An_id_claimed_by_another_plugin_throws_and_the_first_stays_registered()
    {
        var builder = new CachingBuilder(new ServiceCollection());
        builder.AddValueSerializer(AcmeFormats.Name, AcmeFormats.ValueIds, _ => new AcmeValueSerializer());
        var before = Snapshot(builder);

        var act = () => builder.AddValueSerializer("rival", [0x101, 0x102], _ => new AcmeValueSerializer());

        act.Should().Throw<ArgumentException>().WithMessage("*0x101*acme*");
        Snapshot(builder).Should().Equal(before, ReferenceEquals);
    }

    [Theory]
    [InlineData(0x05)]
    [InlineData(0x10)]
    [InlineData(0x1E)]
    [InlineData(0x17F)]
    [InlineData(0x180)]
    [InlineData(0x1FF)]
    public void Every_documented_range_is_open_to_a_plugin(int id)
    {
        var builder = new CachingBuilder(new ServiceCollection());

        var act = () => builder.AddValueSerializer("other", [id], _ => new AcmeValueSerializer(ids: [id]));

        act.Should().NotThrow();
    }

    [Fact]
    public void A_value_id_may_equal_an_event_id_because_they_are_separate_namespaces()
    {
        var builder = new CachingBuilder(new ServiceCollection());
        builder.AddValueSerializer("first", [0x11], _ => new AcmeValueSerializer(ids: [0x11]));

        var act = () => builder.AddEventFormat(AcmeFormats.Name, AcmeFormats.Event, _ => new AcmeEventFormat(), write: false);

        act.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(RejectedEventFormats))]
    public void A_rejected_event_format_throws_at_the_call_and_changes_nothing(string name, int id, bool write, Type expected)
    {
        var builder = new CachingBuilder(new ServiceCollection());
        builder.AddEventFormat(AcmeFormats.Name, AcmeFormats.Event, _ => new AcmeEventFormat(), write: true);
        var before = Snapshot(builder);

        var act = () => builder.AddEventFormat(name, id, _ => new AcmeEventFormat((byte)id), write);

        act.Should().Throw<Exception>().Which.Should().BeOfType(expected);
        Snapshot(builder).Should().Equal(before, ReferenceEquals);
    }

    [Fact]
    public void One_writer_and_a_read_only_second_format_are_accepted_in_either_order()
    {
        var writerFirst = new CachingBuilder(new ServiceCollection());
        var readerFirst = new CachingBuilder(new ServiceCollection());

        var act = () =>
        {
            writerFirst.AddEventFormat(AcmeFormats.Name, AcmeFormats.Event, _ => new AcmeEventFormat(), write: true);
            writerFirst.AddEventFormat("old", 0x12, _ => new AcmeEventFormat(0x12), write: false);
            readerFirst.AddEventFormat("old", 0x12, _ => new AcmeEventFormat(0x12), write: false);
            readerFirst.AddEventFormat(AcmeFormats.Name, AcmeFormats.Event, _ => new AcmeEventFormat(), write: true);
        };

        act.Should().NotThrow();
    }

    private static List<ServiceDescriptor> Snapshot(CachingBuilder builder) => [.. builder.Services];
}
