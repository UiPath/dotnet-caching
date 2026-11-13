using System.Text;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.Redis;

public class WriteReportTests
{
    private static readonly HashEntry[] Three = [new("a", "1"), new("b", "22"), new("c", "333")];

    [Fact]
    public void A_merge_within_the_limits_carries_each_field_as_the_bytes_redis_holds()
    {
        var report = new WriteReport(new PeerFieldLimits(3, 1024));

        report.Capture(Three, HashCacheSetOption.HashReplace);

        report.Fields.Should().BeEquivalentTo(new Dictionary<string, byte[]?> { ["a"] = Bytes("1"), ["b"] = Bytes("22"), ["c"] = Bytes("333") });
    }

    [Fact]
    public void The_metadata_entry_is_not_a_field()
    {
        var report = new WriteReport(new PeerFieldLimits(1, 1024));

        report.Capture([new("a", "1"), new("_metadata_", "{}")], HashCacheSetOption.HashReplace);

        report.Fields.Should().BeEquivalentTo(new Dictionary<string, byte[]?> { ["a"] = Bytes("1") });
    }

    [Fact]
    public void A_replacement_of_the_key_carries_no_fields()
    {
        var report = new WriteReport(new PeerFieldLimits(16, 1024));

        report.Capture(Three, HashCacheSetOption.KeyReplace);

        report.Fields.Should().BeNull();
    }

    [Fact]
    public void A_merge_of_more_fields_than_the_limit_carries_none()
    {
        var report = new WriteReport(new PeerFieldLimits(2, 1024));

        report.Capture(Three, HashCacheSetOption.HashReplace);

        report.Fields.Should().BeNull();
    }

    [Fact]
    public void A_merge_of_more_bytes_than_the_limit_carries_none_and_one_of_exactly_the_limit_carries_all()
    {
        // Names and values: 1+1, 1+2, 1+3 bytes.
        var over = new WriteReport(new PeerFieldLimits(16, 8));
        var exact = new WriteReport(new PeerFieldLimits(16, 9));

        over.Capture(Three, HashCacheSetOption.HashReplace);
        exact.Capture(Three, HashCacheSetOption.HashReplace);

        over.Fields.Should().BeNull();
        exact.Fields.Should().HaveCount(3);
    }

    [Fact]
    public void A_field_is_a_copy_of_the_bytes_the_write_holds()
    {
        var held = new byte[] { 1, 2, 3 };
        var report = new WriteReport(new PeerFieldLimits(16, 1024));

        report.Capture([new("a", held)], HashCacheSetOption.HashReplace);
        held[0] = 9;

        report.Fields!["a"].Should().Equal(1, 2, 3);
    }

    [Fact]
    public void A_merge_with_no_fields_carries_none()
    {
        var report = new WriteReport(new PeerFieldLimits(16, 1024));

        report.Capture([new("_metadata_", "{}")], HashCacheSetOption.HashReplace);

        report.Fields.Should().BeNull();
    }

    [Fact]
    public void A_field_over_the_limit_is_not_copied()
    {
        var huge = new byte[8 * 1024 * 1024];
        HashEntry[] entries = [new("a", "1"), new("big", huge)];
        new WriteReport(new PeerFieldLimits(16, 1024)).Capture(entries, HashCacheSetOption.HashReplace);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var report = new WriteReport(new PeerFieldLimits(16, 1024));
        report.Capture(entries, HashCacheSetOption.HashReplace);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        report.Fields.Should().BeNull();
        allocated.Should().BeLessThan(64 * 1024);
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
