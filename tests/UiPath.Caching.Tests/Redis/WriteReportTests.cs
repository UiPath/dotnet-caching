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

        report.Fields.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = Base64("1"), ["b"] = Base64("22"), ["c"] = Base64("333") });
    }

    [Fact]
    public void The_metadata_entry_is_not_a_field()
    {
        var report = new WriteReport(new PeerFieldLimits(1, 1024));

        report.Capture([new("a", "1"), new("_metadata_", "{}")], HashCacheSetOption.HashReplace);

        report.Fields.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = Base64("1") });
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
    public void A_merge_with_no_fields_carries_none()
    {
        var report = new WriteReport(new PeerFieldLimits(16, 1024));

        report.Capture([new("_metadata_", "{}")], HashCacheSetOption.HashReplace);

        report.Fields.Should().BeNull();
    }

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
