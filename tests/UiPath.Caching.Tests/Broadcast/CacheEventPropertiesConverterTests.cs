using System.Text;
using System.Text.Json;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Tests.Broadcast;

public class CacheEventPropertiesConverterTests
{
    private static readonly DateTimeOffset Deadline = new(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));

    // Reached by name: the CloudEvents assembly compiles the same internal type, so naming it here would be ambiguous.
    private static JsonSerializerOptions Options { get; } = (JsonSerializerOptions)typeof(CacheEventFormatter).Assembly
        .GetType("UiPath.Caching.Broadcast.CacheEventJson")!.GetProperty("Options")!.GetValue(null)!;

    [Fact]
    public void Version_reads_as_long()
    {
        var props = Read("""{"_version_":42}""");

        props["_version_"].Should().BeOfType<long>().Which.Should().Be(42);
    }

    [Fact]
    public void Expirations_read_as_DateTimeOffset()
    {
        var text = JsonSerializer.Serialize(Deadline);
        var props = Read("""{"_expiration_":@,"_peerExpiration_":@}""".Replace("@", text));

        props["_expiration_"].Should().BeOfType<DateTimeOffset>().Which.Should().Be(Deadline);
        props["_peerExpiration_"].Should().BeOfType<DateTimeOffset>().Which.Should().Be(Deadline);
    }

    [Fact]
    public void Metadata_reads_as_string_dictionary()
    {
        var props = Read("""{"_metadata_":{"a":"1","b":null},"_peerMetadata_":{"c":"2"}}""");

        props["_metadata_"].Should().BeOfType<Dictionary<string, string?>>().Which.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "1", ["b"] = null });
        props["_peerMetadata_"].Should().BeOfType<Dictionary<string, string?>>().Which.Should().BeEquivalentTo(new Dictionary<string, string?> { ["c"] = "2" });
    }

    [Fact]
    public void Peer_fields_read_as_a_byte_dictionary_that_decodes_on_first_use()
    {
        byte[] first = [0x02, 0x00, 0xFF];
        var props = Read("""{"_peerFields_":{"a":"@","b":null,"c":""}}""".Replace("@", Convert.ToBase64String(first)));

        var fields = props["_peerFields_"].Should().BeAssignableTo<IReadOnlyDictionary<string, byte[]?>>().Which;
        fields.Keys.Should().Equal("a", "b", "c");
        fields["a"].Should().Equal(first);
        fields["b"].Should().BeNull();
        fields["c"].Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Peer_fields_decode_base64_with_plus_and_slash_in_plain_and_escaped_form(bool escaped)
    {
        var props = Read("""{"_peerFields_":{"a":"@"}}""".Replace("@", Base64(escaped)));

        var fields = props["_peerFields_"].Should().BeAssignableTo<IReadOnlyDictionary<string, byte[]?>>().Which;
        fields["a"].Should().Equal(Convert.FromBase64String("+/+/"));
    }

    [Fact]
    public void Unknown_keys_stay_JsonElement()
    {
        var props = Read("""{"custom":{"a":1},"n":null,"_version_":1}""");

        props["custom"].Should().BeOfType<JsonElement>().Which.ValueKind.Should().Be(JsonValueKind.Object);
        props["n"].Should().BeNull();
    }

    [Theory]
    [InlineData("_version_", "\"7\"")]
    [InlineData("_version_", "1.5")]
    [InlineData("_version_", "9223372036854775808")]
    [InlineData("_expiration_", "\"soon\"")]
    [InlineData("_metadata_", """{"a":1}""")]
    [InlineData("_peerFields_", "\"x\"")]
    [InlineData("_peerFields_", """{"a":1}""")]
    public void A_known_key_with_another_shape_stays_JsonElement_so_the_fallback_decides(string key, string value)
    {
        var props = Read($$$"""{"{{{key}}}":{{{value}}},"_peerMetadata_":{"k":"v"}}""");

        props[key].Should().BeOfType<JsonElement>().Which.GetRawText().Should().Be(value);
        props["_peerMetadata_"].Should().BeOfType<Dictionary<string, string?>>().Which.Should().Equal(new Dictionary<string, string?> { ["k"] = "v" });
    }

    [Fact]
    public void A_bad_base64_entry_is_not_checked_by_the_read_and_fails_the_first_use()
    {
        var props = Read("""{"_peerFields_":{"a":"AQ==","b":"!"},"_version_":3}""");

        var fields = props["_peerFields_"].Should().BeAssignableTo<IReadOnlyDictionary<string, byte[]?>>().Which;
        fields.Count.Should().Be(2);
        fields.Invoking(f => f.ToList()).Should().Throw<FormatException>();
        fields.Invoking(f => f["a"]).Should().Throw<FormatException>();
        props["_version_"].Should().Be(3L);
    }

    [Fact]
    public void With_a_duplicate_key_the_last_one_wins()
    {
        Read("""{"_version_":1,"_version_":2}""")["_version_"].Should().Be(2L);
        Read("""{"_version_":1,"_version_":"x"}""")["_version_"].Should().BeOfType<JsonElement>();
        Read("""{"_version_":"x","_version_":2}""")["_version_"].Should().Be(2L);
    }

    [Fact]
    public void A_value_after_one_that_did_not_fit_still_reads_typed()
    {
        var props = Read("""{"_peerFields_":{"a":1},"_version_":3}""");

        props["_peerFields_"].Should().BeOfType<JsonElement>();
        props["_version_"].Should().Be(3L);
    }

    [Fact]
    public void Write_output_is_byte_identical_to_default_options()
    {
        var data = new CacheEventData("k", new Dictionary<string, object?>
        {
            ["_version_"] = 5L,
            ["_peerFields_"] = new Dictionary<string, string?> { ["a"] = "AQ==", ["b"] = null },
            ["_peerExpiration_"] = Deadline,
            ["_peerMetadata_"] = new Dictionary<string, string?> { ["m"] = null },
            ["custom"] = new Dictionary<string, object?> { ["x"] = 1 },
            ["n"] = null,
        });

        JsonSerializer.SerializeToUtf8Bytes(data, Options).Should().Equal(JsonSerializer.SerializeToUtf8Bytes(data));
    }

    [Fact]
    public void A_decoded_event_encodes_to_the_bytes_it_came_from()
    {
        var json = """{"Key":"k","Properties":{"_version_":5,"_peerFields_":{"a":"AQ==","b":null},"_peerExpiration_":@,"custom":[1,2]}}""".Replace("@", JsonSerializer.Serialize(Deadline));

        var data = JsonSerializer.Deserialize<CacheEventData>(json, Options);

        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(data)).Should().Be(json);
    }

    [Fact]
    public void The_formatter_decodes_known_properties_as_typed_values()
    {
        var body = """{"Id":"1","Source":"urn:test","Type":"set","Data":{"Key":"k","Properties":{"_version_":5,"_peerFields_":{"a":"AQ==","b":null}}}}"""u8.ToArray();

        var properties = new CacheEventFormatter().Decode(body)!.Data!.Properties!;

        properties["_version_"].Should().BeOfType<long>();
        properties["_peerFields_"].Should().BeAssignableTo<IReadOnlyDictionary<string, byte[]?>>();
    }

    [Fact]
    public void The_converter_names_match_KnownFieldNames()
    {
        var constants = typeof(CacheEventFormatter).Assembly.GetType("UiPath.Caching.Broadcast.CacheEventPropertiesConverter")!
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        constants.Should().Equal(new Dictionary<string, string>
        {
            [nameof(KnownFieldNames.VersionKey)] = KnownFieldNames.VersionKey,
            [nameof(KnownFieldNames.ExpirationKey)] = KnownFieldNames.ExpirationKey,
            [nameof(KnownFieldNames.PeerExpirationKey)] = KnownFieldNames.PeerExpirationKey,
            [nameof(KnownFieldNames.MetadataKey)] = KnownFieldNames.MetadataKey,
            [nameof(KnownFieldNames.PeerMetadataKey)] = KnownFieldNames.PeerMetadataKey,
            [nameof(KnownFieldNames.PeerFieldsKey)] = KnownFieldNames.PeerFieldsKey,
        });
    }

    private static string Base64(bool escaped) =>
        escaped ? "+/+/".Replace("+", @"\u002B").Replace("/", @"\/") : "+/+/";

    private static IDictionary<string, object?> Read(string json) =>
        JsonSerializer.Deserialize<IDictionary<string, object?>>(json, Options)!;
}
