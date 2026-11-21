using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UiPath.Caching.Config;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Negotiation;

/// <summary>A marked proxy negotiates as the default one does, so Auto mode applies its policy checks; an unmarked custom proxy stays in Force.</summary>
public sealed class HeaderFreeNegotiationModeTests
{
    [Fact]
    public void A_marked_proxy_in_auto_mode_refuses_a_read_list_without_json()
    {
        using var provider = Build(new HeaderFreeProxy());

        var act = () => provider.GetRequiredService<ICachePolicyFactory>();

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*json*", "the writer falls back to the envelope until every node reads the format");
    }

    [Fact]
    public void An_unmarked_custom_proxy_is_in_force_mode_so_the_same_read_list_is_accepted()
    {
        using var provider = Build(new EnvelopeProxy());

        provider.GetRequiredService<ICachePolicyFactory>().Should().NotBeNull();
    }

    private static ServiceProvider Build(ISerializerProxy<byte[]> proxy) =>
        new ServiceCollection()
            .AddSingleton(proxy)
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1");
                    b.AddRedis(o => o.FormatNegotiation.Mode = FormatNegotiationMode.Auto);
                    b.AddValueSerializer("fast", [0x10], _ => new PassthroughValueSerializer(0x10));
                },
                o =>
                {
                    o.AppShortName = "headerfreemode";
                    o.Policies["orders"] = new CachePolicy { Serializer = new SerializerPolicy { Write = "fast", Read = ["fast"] } };
                })
            .BuildServiceProvider();
}
