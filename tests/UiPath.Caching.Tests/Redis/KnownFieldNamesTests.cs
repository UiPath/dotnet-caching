using System.Text;
using StackExchange.Redis;

namespace UiPath.Caching.Tests.Redis;

public class KnownFieldNamesTests
{
    public static TheoryData<string> UserFields() =>
    [
        "_metadatb_",
        "_vXrsion_",
        "_Version_",
        "_METADATA_",
        "_version",
        "version_",
        "_version_ ",
        "_metadata_1",
        "metadata",
        "",
        "x",
    ];

    [Fact]
    public void Version_and_metadata_are_system_fields_as_bytes()
    {
        KnownFieldNames.IsSystemField("_version_"u8).Should().BeTrue();
        KnownFieldNames.IsSystemField("_metadata_"u8).Should().BeTrue();
        KnownFieldNames.IsVersion("_version_"u8).Should().BeTrue();
        KnownFieldNames.IsMetadata("_metadata_"u8).Should().BeTrue();
        KnownFieldNames.IsVersion("_metadata_"u8).Should().BeFalse();
        KnownFieldNames.IsMetadata("_version_"u8).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(UserFields))]
    public void Other_fields_are_not_system_fields_as_bytes(string field)
    {
        var bytes = Encoding.UTF8.GetBytes(field).AsSpan();

        KnownFieldNames.IsSystemField(bytes).Should().BeFalse(field);
        KnownFieldNames.IsVersion(bytes).Should().BeFalse(field);
        KnownFieldNames.IsMetadata(bytes).Should().BeFalse(field);
    }

    [Theory]
    [MemberData(nameof(UserFields))]
    public void Byte_and_string_checks_agree(string field)
    {
        KnownFieldNames.IsSystemField(Encoding.UTF8.GetBytes(field).AsSpan()).Should().Be(KnownFieldNames.IsSystemField(field));
    }

    [Fact]
    public void A_name_Redis_hands_back_is_matched_whatever_its_storage()
    {
        RedisValue raw = Encoding.UTF8.GetBytes("_version_");
        RedisValue text = "_version_";

        KnownFieldNames.IsVersion(raw).Should().BeTrue();
        KnownFieldNames.IsVersion(text).Should().BeTrue();
        KnownFieldNames.IsMetadata(raw).Should().BeFalse();
        KnownFieldNames.IsSystemField(raw).Should().BeTrue();
        KnownFieldNames.IsSystemField((RedisValue)Encoding.UTF8.GetBytes("_metadata_")).Should().BeTrue();
        KnownFieldNames.IsSystemField((RedisValue)"_metadata_").Should().BeTrue();
        KnownFieldNames.IsSystemField((RedisValue)Encoding.UTF8.GetBytes("_metadatb_")).Should().BeFalse();
        KnownFieldNames.IsSystemField((RedisValue)"_vXrsion_").Should().BeFalse();
        KnownFieldNames.IsVersion((RedisValue)Encoding.UTF8.GetBytes("_metadata_")).Should().BeFalse();
    }

    [Fact]
    public void A_numeric_or_empty_name_is_not_a_system_field()
    {
        KnownFieldNames.IsSystemField((RedisValue)12).Should().BeFalse();
        KnownFieldNames.IsSystemField(RedisValue.EmptyString).Should().BeFalse();
        KnownFieldNames.IsSystemField(RedisValue.Null).Should().BeFalse();
    }
}
