using UiPath.Caching.Config;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Serialization;

public class ValueFormatResolverTests
{
    private static readonly CachePolicy WritesFast = new() { Serializer = new SerializerPolicy { Write = "fast" } };

    private readonly PassthroughValueSerializer _fast = new(0x10);

    [Fact]
    public void A_policy_without_a_serializer_writes_the_default_policys()
    {
        var sut = Resolver(defaultPolicy: WritesFast);

        var format = sut.For(new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(1) }, forWrite: true);

        format.Writer.Should().BeSameAs(_fast);
    }

    [Fact]
    public void A_policy_that_sets_only_read_still_writes_the_default_policys_serializer()
    {
        var sut = Resolver(defaultPolicy: WritesFast);

        var format = sut.For(new CachePolicy { Serializer = new SerializerPolicy { Read = ["json", "fast"] } }, forWrite: true);

        format.Writer.Should().BeSameAs(_fast);
    }

    [Fact]
    public void A_policy_that_writes_json_wins_over_the_default_policys_serializer()
    {
        var sut = Resolver(defaultPolicy: WritesFast);

        sut.For(new CachePolicy { Serializer = new SerializerPolicy { Write = "json" } }, forWrite: true).Writer.Should().BeNull();
    }

    [Fact]
    public void A_policy_that_names_its_own_serializer_wins_over_the_default_policys()
    {
        var other = new PassthroughValueSerializer(0x12);
        var sut = Resolver(defaultPolicy: WritesFast, other: other);

        sut.For(new CachePolicy { Serializer = new SerializerPolicy { Write = "other" } }, forWrite: true).Writer.Should().BeSameAs(other);
    }

    [Fact]
    public void A_policy_with_a_resolved_slot_keeps_it_over_the_default_policys()
    {
        var other = new PassthroughValueSerializer(0x12);
        var resolved = CachePolicyMerger.WithWriteSerializer(new CachePolicy { Serializer = new SerializerPolicy { Write = "other" } }, other);
        var sut = Resolver(defaultPolicy: WritesFast, other: other);

        sut.For(resolved, forWrite: true).Writer.Should().BeSameAs(other);
    }

    [Fact]
    public void A_read_takes_the_default_policys_write_for_the_gate_of_a_policy_without_one()
    {
        var sut = Resolver(proxy: new RawByteSerializerProxy(), defaultPolicy: WritesFast);

        sut.For(new CachePolicy(), forWrite: false).Sniff.Should().BeTrue();
        sut.For(new CachePolicy { Serializer = new SerializerPolicy { Write = "json" } }, forWrite: false).Sniff.Should().BeFalse();
    }

    [Fact]
    public void The_json_default_resolves_without_allocating()
    {
        var sut = Resolver();
        var adHoc = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(1) };
        var json = new CachePolicy { Serializer = new SerializerPolicy { Write = "json" } };
        Measure(sut, adHoc, json);

        var allocated = Measure(sut, adHoc, json);

        allocated.Should().Be(0);
    }

    private static long Measure(ValueFormatResolver sut, CachePolicy adHoc, CachePolicy json)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = sut.For(null, forWrite: true);
            _ = sut.For(adHoc, forWrite: true);
            _ = sut.For(json, forWrite: false);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private ValueFormatResolver Resolver(ISerializerProxy<byte[]>? proxy = null, CachePolicy? defaultPolicy = null, ICacheValueSerializer? other = null)
    {
        List<(string, ICacheValueSerializer, bool)> entries = [("fast", _fast, false)];
        if (other is not null)
        {
            entries.Add(("other", other, false));
        }

        return new(proxy ?? new SystemJsonByteSerializerProxy(), new ValueSerializerRegistry(entries), defaultPolicy ?? new CachePolicy());
    }
}
