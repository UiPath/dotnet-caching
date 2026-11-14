using Microsoft.Extensions.Configuration;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests;

public class FailSafePolicyTests
{
    [Fact]
    public void Fail_safe_is_off_and_unset_by_default()
    {
        var policy = new CachePolicy();

        policy.FailSafeEnabled.Should().BeNull();
        policy.FailSafeMaxStaleAge.Should().BeNull();
        policy.FailSafeCooldown.Should().BeNull();
        policy.FailSafeTimeout.Should().BeNull();
        new GeneratedValue<string>("v").SkipFailSafe.Should().BeFalse();
    }

    [Fact]
    public void Named_fail_safe_fields_win_and_unset_ones_inherit()
    {
        var defaults = new CachePolicy
        {
            FailSafeEnabled = true,
            FailSafeMaxStaleAge = TimeSpan.FromHours(2),
            FailSafeCooldown = TimeSpan.FromSeconds(10),
            FailSafeTimeout = TimeSpan.FromSeconds(1),
        };

        var merged = CachePolicyMerger.Merge(new CachePolicy { FailSafeEnabled = false, FailSafeTimeout = TimeSpan.FromSeconds(2) }, defaults);

        merged.FailSafeEnabled.Should().BeFalse();
        merged.FailSafeTimeout.Should().Be(TimeSpan.FromSeconds(2));
        merged.FailSafeMaxStaleAge.Should().Be(TimeSpan.FromHours(2));
        merged.FailSafeCooldown.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void The_merger_carries_every_policy_field()
    {
        // A value that skips fail-safe is written under a merged twin of its policy; a field the merger forgot would be lost there.
        var empty = new CachePolicy();
        var merged = CachePolicyMerger.Merge(new CachePolicy(), new CachePolicy
        {
            LocalExpiration = TimeSpan.FromSeconds(1), LocalExpirationDisconnected = TimeSpan.FromSeconds(1), DistributedExpiration = TimeSpan.FromSeconds(1),
            FactoryTimeout = TimeSpan.FromSeconds(1), BackgroundDistributedWrite = true, RefreshLocalOnPeerWrite = true, RefreshLocalRetryWindow = TimeSpan.FromSeconds(1),
            JitterMaxDuration = TimeSpan.FromSeconds(1), RehydrateEnabled = true, Rehydrate = new RehydrateOptions(), Lock = new LockProfile(),
            FailSafeEnabled = true, FailSafeMaxStaleAge = TimeSpan.FromSeconds(1), FailSafeCooldown = TimeSpan.FromSeconds(1), FailSafeTimeout = TimeSpan.FromSeconds(1),
            Serializer = new SerializerPolicy(),
        });

        foreach (var property in typeof(CachePolicy).GetProperties())
        {
            property.GetValue(empty).Should().BeNull($"{property.Name} starts unset");
            property.GetValue(merged).Should().NotBeNull($"the merger carries {property.Name}");
        }
    }

    [Theory]
    [InlineData(nameof(CachePolicy.FailSafeMaxStaleAge))]
    [InlineData(nameof(CachePolicy.FailSafeCooldown))]
    [InlineData(nameof(CachePolicy.FailSafeTimeout))]
    public void A_negative_fail_safe_duration_is_refused(string field)
    {
        var negative = TimeSpan.FromSeconds(-1);
        var policy = field switch
        {
            nameof(CachePolicy.FailSafeMaxStaleAge) => new CachePolicy { FailSafeMaxStaleAge = negative },
            nameof(CachePolicy.FailSafeCooldown) => new CachePolicy { FailSafeCooldown = negative },
            _ => new CachePolicy { FailSafeTimeout = negative },
        };

        var act = () => new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { ["p"] = policy }, null);

        act.Should().Throw<OptionsValidationException>().WithMessage($"*Policies['p'].{field}*");
    }

    [Fact]
    public void Fail_safe_binds_from_the_policies_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Caching:AppShortName"] = "app",
                ["Caching:Policies:routing:FailSafeEnabled"] = "true",
                ["Caching:Policies:routing:FailSafeMaxStaleAge"] = "1.00:00:00",
                ["Caching:Policies:routing:FailSafeCooldown"] = "00:00:30",
                ["Caching:Policies:routing:FailSafeTimeout"] = "00:00:02",
            })
            .Build();

        var options = configuration.GetSection("Caching").Get<CacheOptions>()!;

        var policy = options.Policies["routing"];
        policy.FailSafeEnabled.Should().BeTrue();
        policy.FailSafeMaxStaleAge.Should().Be(TimeSpan.FromDays(1));
        policy.FailSafeCooldown.Should().Be(TimeSpan.FromSeconds(30));
        policy.FailSafeTimeout.Should().Be(TimeSpan.FromSeconds(2));
    }
}
