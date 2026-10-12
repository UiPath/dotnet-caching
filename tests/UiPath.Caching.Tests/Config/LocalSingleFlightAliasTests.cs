#pragma warning disable CS0618 // The obsolete lock names are what these tests exercise.
using Microsoft.Extensions.Configuration;

namespace UiPath.Caching.Tests.Config;

public class LocalSingleFlightAliasTests
{
    [Fact]
    public void The_old_names_read_and_write_the_new_ones()
    {
        var memory = new InMemoryCacheOptions { LocalLockEnabled = false, LocalLockTimeout = TimeSpan.FromSeconds(2) };
        var redis = new InMemoryRedisCacheOptions { LocalLockEnabled = false, LocalLockTimeout = TimeSpan.FromSeconds(2) };
        var viaInterface = DisabledThroughTheInterface(new InMemoryRedisCacheOptions());
        var profile = new LockProfile { LocalLockEnabled = false, LocalLockTimeout = TimeSpan.FromSeconds(2) };

        memory.LocalSingleFlightEnabled.Should().BeFalse();
        memory.LocalSingleFlightTimeout.Should().Be(TimeSpan.FromSeconds(2));
        redis.LocalSingleFlightEnabled.Should().BeFalse();
        redis.LocalSingleFlightTimeout.Should().Be(TimeSpan.FromSeconds(2));
        viaInterface.LocalSingleFlightEnabled.Should().BeFalse();
        profile.LocalSingleFlightEnabled.Should().BeFalse();
        profile.LocalSingleFlightTimeout.Should().Be(TimeSpan.FromSeconds(2));
        profile.LocalLockEnabled.Should().BeFalse();
    }

    [Fact]
    public void An_old_key_in_configuration_still_binds()
    {
        var options = Bind<InMemoryRedisCacheOptions>(("LocalLockEnabled", "false"), ("LocalLockTimeout", "00:00:02"));

        options.LocalSingleFlightEnabled.Should().BeFalse();
        options.LocalSingleFlightTimeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_new_key_wins_when_both_are_configured()
    {
        var memory = Bind<InMemoryCacheOptions>(("LocalLockEnabled", "false"), ("LocalSingleFlightEnabled", "true"), ("LocalLockTimeout", "00:00:09"), ("LocalSingleFlightTimeout", "00:00:02"));
        var redis = Bind<InMemoryRedisCacheOptions>(("LocalLockEnabled", "false"), ("LocalSingleFlightEnabled", "true"), ("LocalLockTimeout", "00:00:09"), ("LocalSingleFlightTimeout", "00:00:02"));
        var profile = Bind<LockProfile>(("LocalLockEnabled", "false"), ("LocalSingleFlightEnabled", "true"), ("LocalLockTimeout", "00:00:09"), ("LocalSingleFlightTimeout", "00:00:02"));

        memory.LocalSingleFlightEnabled.Should().BeTrue();
        memory.LocalSingleFlightTimeout.Should().Be(TimeSpan.FromSeconds(2));
        redis.LocalSingleFlightEnabled.Should().BeTrue();
        redis.LocalSingleFlightTimeout.Should().Be(TimeSpan.FromSeconds(2));
        profile.LocalSingleFlightEnabled.Should().BeTrue();
        profile.LocalSingleFlightTimeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    private static T Bind<T>(params (string Key, string Value)[] values)
        where T : new() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build()
            .Get<T>()!;

    private static IMultilayerCacheOptions DisabledThroughTheInterface(IMultilayerCacheOptions options)
    {
        options.LocalLockEnabled = false;
        return options;
    }
}
