using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Tests.MessagePack;

public class MessagePackFormatsTests
{
    [Fact]
    public void The_ids_are_the_ones_on_the_wire()
    {
        MessagePackFormats.Value.Should().Be(0x02);
        MessagePackFormats.ValueLz4.Should().Be(0x03);
        MessagePackFormats.Event.Should().Be(0x02);
        MessagePackFormats.ValueIds.Should().Equal(0x02, 0x03);
    }

    [Fact]
    public void The_core_event_byte_matches_until_the_event_seam_replaces_it() =>
        BinaryEventSettings.FormatId.Should().Be(MessagePackFormats.Event);

    [Fact]
    public void A_second_serializer_under_another_name_clashes_on_the_ids_at_the_call()
    {
        var builder = new CachingBuilder(new ServiceCollection());
        builder.AddMessagePackSerializer();

        var act = () => builder.AddMessagePackSerializer("other");

        act.Should().Throw<ArgumentException>().WithParameterName("formatIds").WithMessage("*0x02 is already claimed*");
    }
}
