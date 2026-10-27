using Microsoft.Extensions.Configuration;

namespace UiPath.Caching.Tests.Config;

public class RedisCacheOptionsBindingTests
{
    [Theory]
    [InlineData(null, RedisReadFrom.Master)]
    [InlineData("Master", RedisReadFrom.Master)]
    [InlineData("Replica", RedisReadFrom.Replica)]
    public void ReadFrom_binds_from_configuration(string? configured, RedisReadFrom expected)
    {
        var values = configured is null ? [] : new Dictionary<string, string?> { ["Redis:ReadFrom"] = configured };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new RedisCacheOptions();

        configuration.GetSection("Redis").Bind(options);

        options.ReadFrom.Should().Be(expected);
    }
}
