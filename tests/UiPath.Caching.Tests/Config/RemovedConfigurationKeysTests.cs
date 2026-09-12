using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Config;

public class RemovedConfigurationKeysTests
{
    [Theory]
    [InlineData("PrimaryMaxExpiration", "LocalMaxExpiration")]
    [InlineData("PrimaryMaxExpirationDisconnected", "LocalMaxExpirationDisconnected")]
    [InlineData("UsePrimaryOnlyWhenDisconnected", "UseLocalOnlyWhenDisconnected")]
    public void AddInMemoryRedis_from_configuration_throws_on_a_renamed_key(string old, string replacement)
    {
        var act = () => Register(b => b.AddInMemoryRedis(), $"Caching:InMemoryRedis:{old}", "01:00:00");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Caching:InMemoryRedis").And.Contain(old).And.Contain(replacement);
    }

    [Theory]
    [InlineData("PrimaryMaxExpiration", "LocalMaxExpiration")]
    [InlineData("PrimaryMaxExpirationDisconnected", "LocalMaxExpirationDisconnected")]
    [InlineData("UsePrimaryOnlyWhenDisconnected", "UseLocalOnlyWhenDisconnected")]
    public void AddMemory_from_configuration_throws_on_a_renamed_key(string old, string replacement)
    {
        var act = () => Register(b => b.AddMemory(), $"Caching:InMemory:{old}", "01:00:00");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Caching:InMemory").And.Contain(old).And.Contain(replacement);
    }

    [Fact]
    public void AddRedisConnection_from_configuration_throws_on_the_removed_socket_manager_key()
    {
        var act = () => Register(b => b.AddRedisConnection(), "Caching:Connections:Redis:ThreadPoolSocketManager", "true");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Caching:Connections:Redis").And.Contain("ThreadPoolSocketManager").And.Contain("delete it");
    }

    [Fact]
    public void AddRedisConnection_with_section_and_configure_throws_on_the_removed_socket_manager_key()
    {
        var act = () => Register(
            b => b.AddRedisConnection("Connections:Redis", _ => { }),
            "Caching:Connections:Redis:ThreadPoolSocketManager",
            "false");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("ThreadPoolSocketManager");
    }

    [Fact]
    public void A_removed_key_with_a_null_value_is_refused()
    {
        // JSON "PrimaryMaxExpiration": null reaches the binder as a key with no value; in 1.x that null
        // removed the default cap, so it must not pass as absent.
        var act = () => Register(b => b.AddInMemoryRedis(), ("Caching:InMemoryRedis:PrimaryMaxExpiration", null));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("PrimaryMaxExpiration");
    }

    [Fact]
    public void A_removed_key_is_matched_regardless_of_case()
    {
        var act = () => Register(b => b.AddInMemoryRedis(), ("Caching:InMemoryRedis:primarymaxexpiration", "01:00:00"));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("PrimaryMaxExpiration");
    }

    [Fact]
    public void A_removed_key_is_refused_even_when_the_provider_is_disabled()
    {
        var act = () => Register(b => b.AddInMemoryRedis(),
            ("Caching:InMemoryRedis:Enabled", "false"),
            ("Caching:InMemoryRedis:PrimaryMaxExpiration", "01:00:00"));

        act.Should().Throw<InvalidOperationException>("a disabled provider is often the non-production profile of one that is on elsewhere");
    }

    [Fact]
    public void Every_removed_key_present_is_named_in_one_message()
    {
        var act = () => Register(b => b.AddInMemoryRedis(),
            ("Caching:InMemoryRedis:PrimaryMaxExpiration", "01:00:00"),
            ("Caching:InMemoryRedis:UsePrimaryOnlyWhenDisconnected", "true"));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("PrimaryMaxExpiration").And.Contain("UsePrimaryOnlyWhenDisconnected");
    }

    [Fact]
    public void The_renamed_keys_bind_without_complaint()
    {
        using var provider = Register(b => b.AddInMemoryRedis(),
            ("Caching:InMemoryRedis:LocalMaxExpiration", "01:00:00"),
            ("Caching:InMemoryRedis:LocalMaxExpirationDisconnected", "00:00:10"),
            ("Caching:InMemoryRedis:UseLocalOnlyWhenDisconnected", "true"));

        var options = provider.GetRequiredService<IOptions<InMemoryRedisCacheOptions>>().Value;
        options.LocalMaxExpiration.Should().Be(TimeSpan.FromHours(1));
        options.LocalMaxExpirationDisconnected.Should().Be(TimeSpan.FromSeconds(10));
        options.UseLocalOnlyWhenDisconnected.Should().BeTrue();
    }

    [Fact]
    public void A_section_with_no_removed_key_is_left_alone()
    {
        using var provider = Register(b => b.AddInMemoryRedis().AddRedisConnection(),
            ("Caching:InMemoryRedis:DefaultExpiration", "00:05:00"),
            ("Caching:Connections:Redis:ProfilerEnabled", "false"));

        provider.GetRequiredService<IOptions<InMemoryRedisCacheOptions>>().Value.DefaultExpiration
            .Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void The_code_only_overloads_are_not_guarded_because_the_compiler_already_is()
    {
        // No configuration at all: nothing to scan, and the property no longer exists to assign.
        var act = () => Register(b => b.AddInMemoryRedis(opt => opt.LocalMaxExpiration = TimeSpan.FromHours(1)));

        act.Should().NotThrow();
    }

    private static ServiceProvider Register(Action<ICachingBuilder> configure, string key, string value) =>
        Register(configure, (key, value));

    private static ServiceProvider Register(Action<ICachingBuilder> configure, params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, configure);
        return services.BuildServiceProvider();
    }
}
