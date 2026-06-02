using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests;

public class DefaultCachePolicyFactoryTests
{
    [Fact]
    public void Resolve_returns_null_when_name_not_registered()
    {
        var defaults = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(5) };
        var factory = new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy>(), defaults);

        factory.Resolve("nothing-registered").Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_null_when_policyName_is_null()
    {
        var defaults = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(5) };
        var factory = new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy>(), defaults);

        factory.Resolve(null!).Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_null_when_policyName_is_empty()
    {
        var defaults = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(5) };
        var factory = new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy>(), defaults);

        factory.Resolve(string.Empty).Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_pre_merged_named_policy()
    {
        var named = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        var defaults = new CachePolicy { DistributedExpiration = TimeSpan.FromMinutes(10) };

        var factory = new DefaultCachePolicyFactory(
            new Dictionary<string, CachePolicy> { ["clients-cache"] = named },
            defaults);

        var policy = factory.Resolve("clients-cache");

        policy.Should().NotBeNull();
        policy!.LocalExpiration.Should().Be(TimeSpan.FromMinutes(1));
        policy.DistributedExpiration.Should().Be(TimeSpan.FromMinutes(10), "inherited from default");
    }

    [Fact]
    public void Resolve_returns_same_instance_for_repeated_calls()
    {
        var named = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        var factory = new DefaultCachePolicyFactory(
            new Dictionary<string, CachePolicy> { ["x"] = named },
            defaultPolicy: null);

        var first = factory.Resolve("x");
        var second = factory.Resolve("x");

        first.Should().BeSameAs(second, "pre-merged once at construction; the hot path is a dictionary lookup, no allocation");
    }

    [Fact]
    public void Constructor_throws_clear_message_on_case_only_duplicate_keys()
    {
        var policies = new[]
        {
            new KeyValuePair<string, CachePolicy>("Clients", new CachePolicy()),
            new KeyValuePair<string, CachePolicy>("clients", new CachePolicy()),
        };

        var act = () => new DefaultCachePolicyFactory(policies, defaultPolicy: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*case*Clients*");
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        var named = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };

        var factory = new DefaultCachePolicyFactory(
            new Dictionary<string, CachePolicy> { ["Clients-Cache"] = named },
            defaultPolicy: null);

        factory.Resolve("clients-cache")!.LocalExpiration.Should().Be(TimeSpan.FromMinutes(1));
        factory.Resolve("CLIENTS-CACHE")!.LocalExpiration.Should().Be(TimeSpan.FromMinutes(1));
    }
}
