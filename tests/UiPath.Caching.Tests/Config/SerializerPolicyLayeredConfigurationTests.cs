using System.Text;
using Microsoft.Extensions.Configuration;

namespace UiPath.Caching.Tests.Config;

/// <summary>Pins how the configuration binder layers <c>Serializer.Read</c>, which the docs describe; a change here is a change to those docs.</summary>
public class SerializerPolicyLayeredConfigurationTests
{
    [Fact]
    public void A_shorter_override_replaces_only_the_indexes_it_sets()
    {
        var read = Bind(Layer("""["a", "b"]"""), Layer("""["json"]""")).Read;

        read.Should().Equal("json", "b");
    }

    [Fact]
    public void An_override_that_sets_every_index_replaces_the_list()
    {
        Bind(Layer("""["a", "b"]"""), Layer("""["json", "x"]""")).Read.Should().Equal("json", "x");
        Bind(Layer("""["a", "b"]"""), Layer("""["x", "y", "z"]""")).Read.Should().Equal("x", "y", "z");
    }

    [Fact]
    public void An_empty_override_binds_as_inherit()
    {
        Bind(Layer("""["a", "b"]"""), Layer("[]")).Read.Should().Equal("a", "b");
        Bind(Layer("[]")).Read.Should().BeNull();
    }

    [Fact]
    public void A_single_layer_binds_the_list_as_written()
    {
        Bind(Layer("""["json", "fast"]""")).Read.Should().Equal("json", "fast");
    }

    private static string Layer(string read) => $$"""{ "Serializer": { "Read": {{read}} } }""";

    private static SerializerPolicy Bind(params string[] layers)
    {
        var builder = new ConfigurationBuilder();
        foreach (var layer in layers)
        {
            builder.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(layer)));
        }

        return builder.Build().GetSection("Serializer").Get<SerializerPolicy>()!;
    }
}
