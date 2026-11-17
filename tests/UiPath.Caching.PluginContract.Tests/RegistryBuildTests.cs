using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.PluginContract.Tests.Acme;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PluginContract.Tests;

public class RegistryBuildTests
{
    public static TheoryData<int[]> ReportedIds() => new()
    {
        { [0x100] },
        { [0x100, 0x101, 0x102] },
        { [0x100, 0x102] },
    };

    [Theory]
    [MemberData(nameof(ReportedIds))]
    public void A_serializer_that_reports_other_ids_than_were_declared_fails_the_build_and_is_disposed(int[] reported)
    {
        var built = new List<AcmeValueSerializer>();
        using var node = Node(_ =>
        {
            var serializer = new AcmeValueSerializer(ids: reported);
            built.Add(serializer);
            return serializer;
        });

        var act = () => node.GetRequiredService<ICachePolicyFactory>().Resolve("acme");

        act.Should().Throw<InvalidOperationException>().WithMessage("*acme*0x100*");
        built.Should().ContainSingle().Which.Disposals.Should().Be(1, "a build that throws disposes what it created");
    }

    [Fact]
    public void A_matching_serializer_is_built_once_and_disposed_with_the_container()
    {
        var built = new List<AcmeValueSerializer>();
        var node = Node(_ =>
        {
            var serializer = new AcmeValueSerializer();
            built.Add(serializer);
            return serializer;
        });

        _ = node.GetRequiredService<ICachePolicyFactory>().Resolve("acme");
        _ = node.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var disposalsBefore = built.Sum(s => s.Disposals);
        node.Dispose();

        built.Should().ContainSingle();
        disposalsBefore.Should().Be(0);
        built[0].Disposals.Should().Be(1);
    }

    private static ServiceProvider Node(Func<IServiceProvider, AcmeValueSerializer> factory) =>
        new ServiceCollection()
            .AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = "127.0.0.1:1,abortConnect=false");
                    b.AddRedis(o => o.FormatNegotiation.Mode = FormatNegotiationMode.Force);
                    b.AddValueSerializer(AcmeFormats.Name, AcmeFormats.ValueIds, factory);
                },
                o =>
                {
                    o.AppShortName = "contract";
                    o.Policies["acme"] = new CachePolicy { Serializer = new SerializerPolicy { Write = AcmeFormats.Name } };
                })
            .BuildServiceProvider();
}
