using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Distributed;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Config;

public class SerializerPolicyValidationTests
{
    private static readonly FakeValueSerializer Fast = new(0x10);

    [Fact]
    public void Unknown_write_name_fails_when_policies_are_built()
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = Writing("nope") };

        var act = () => new DefaultCachePolicyFactory(policies, null, default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*").WithMessage("*nope*");
    }

    [Fact]
    public void Unknown_write_name_on_the_default_policy_fails()
    {
        var act = () => new DefaultCachePolicyFactory(null, Writing("nope"), default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*default*").WithMessage("*nope*");
    }

    [Fact]
    public void Unknown_read_name_fails()
    {
        var policies = new Dictionary<string, CachePolicy>
        {
            ["orders"] = new() { Serializer = new SerializerPolicy { Read = ["json", "nope"] } },
        };

        var act = () => new DefaultCachePolicyFactory(policies, null, default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*").WithMessage("*nope*");
    }

    [Fact]
    public void A_name_inherited_from_the_default_is_validated_for_the_policy_that_inherits_it()
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = new() { LocalExpiration = TimeSpan.FromMinutes(1) } };

        var act = () => new DefaultCachePolicyFactory(policies, Writing("nope"), default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*nope*");
    }

    [Fact]
    public void No_registry_means_only_json_is_known()
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = Writing("fast") };

        var act = () => new DefaultCachePolicyFactory(policies, null);

        act.Should().Throw<OptionsValidationException>().WithMessage("*fast*");
    }

    [Theory, InlineData(""), InlineData(" "), InlineData("	")]
    public void A_blank_write_name_fails(string write)
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = Writing(write) };

        var act = () => new DefaultCachePolicyFactory(policies, null, default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*").WithMessage("*Serializer.Write*").WithMessage("*blank*");
    }

    [Fact]
    public void A_blank_write_name_does_not_override_an_inherited_serializer()
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = Writing("") };

        var act = () => new DefaultCachePolicyFactory(policies, Writing("fast"), default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*").WithMessage("*Serializer.Write*").WithMessage("*blank*");
    }

    [Fact]
    public void A_blank_write_name_on_the_default_policy_fails()
    {
        var act = () => new DefaultCachePolicyFactory(null, Writing(""), default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*default*").WithMessage("*Serializer.Write*").WithMessage("*blank*");
    }

    [Theory, InlineData(null), InlineData(""), InlineData(" ")]
    public void A_blank_read_name_fails(string? entry)
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = Reading("json", entry!) };

        var act = () => new DefaultCachePolicyFactory(policies, null, default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*").WithMessage("*Serializer.Read*").WithMessage("*blank*");
    }

    [Fact]
    public void An_empty_read_list_fails()
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = Reading() };

        var act = () => new DefaultCachePolicyFactory(policies, null, default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*").WithMessage("*Serializer.Read*").WithMessage("*empty*");
    }

    [Fact]
    public void A_write_name_missing_from_read_fails()
    {
        var policies = new Dictionary<string, CachePolicy>
        {
            ["orders"] = new() { Serializer = new SerializerPolicy { Write = "fast", Read = ["json"] } },
        };

        var act = () => new DefaultCachePolicyFactory(policies, null, default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*").WithMessage("*fast*").WithMessage("*Serializer.Read*");
    }

    [Fact]
    public void Writing_json_by_default_needs_json_in_read()
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = Reading("fast") };

        var act = () => new DefaultCachePolicyFactory(policies, null, default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*").WithMessage("*json*");
    }

    [Fact]
    public void A_write_name_missing_from_the_inherited_read_list_fails()
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = Writing("fast") };

        var act = () => new DefaultCachePolicyFactory(policies, Reading("json"), default, Registry());

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*").WithMessage("*fast*");
    }

    [Fact]
    public void Read_names_match_the_write_name_ignoring_case()
    {
        var policy = new CachePolicy { Serializer = new SerializerPolicy { Write = "FAST", Read = ["JSON", "fast"] } };

        var sut = new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { ["orders"] = policy }, null, default, Registry());

        CachePolicyMerger.WriteSerializerOf(sut.Resolve("orders")!).Should().BeSameAs(Fast);
    }

    [Fact]
    public void A_factory_that_returns_null_names_the_serializer()
    {
        var services = new ServiceCollection();
        services.AddCaching(builder => builder.AddValueSerializer("fast", _ => null!));
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ICachePolicyFactory>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*fast*").WithMessage("*null*");
    }

    [Theory, InlineData(null), InlineData("json"), InlineData("JSON")]
    public void Json_and_unset_need_no_registration(string? write)
    {
        var policy = Writing(write);
        var sut = new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { ["orders"] = policy }, null);

        CachePolicyMerger.WriteSerializerOf(sut.Resolve("orders")!).Should().BeNull();
    }

    [Fact]
    public void A_registered_name_fills_the_resolved_slot()
    {
        var sut = new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { ["orders"] = Writing("FAST") }, null, default, Registry());

        CachePolicyMerger.WriteSerializerOf(sut.Resolve("orders")!).Should().BeSameAs(Fast);
        sut.Resolve("orders")!.Serializer!.Write.Should().Be("FAST");
    }

    [Fact]
    public void A_policy_inherits_the_default_serializer_and_its_slot()
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = new() { LocalExpiration = TimeSpan.FromMinutes(1) } };

        var sut = new DefaultCachePolicyFactory(policies, Writing("fast"), default, Registry());

        CachePolicyMerger.WriteSerializerOf(sut.Resolve("orders")!).Should().BeSameAs(Fast);
        CachePolicyMerger.WriteSerializerOf(sut.Default!).Should().BeSameAs(Fast);
    }

    [Fact]
    public void A_policy_can_write_json_under_a_default_that_does_not()
    {
        var policies = new Dictionary<string, CachePolicy> { ["orders"] = Writing("json") };

        var sut = new DefaultCachePolicyFactory(policies, Writing("fast"), default, Registry());

        CachePolicyMerger.WriteSerializerOf(sut.Resolve("orders")!).Should().BeNull();
        CachePolicyMerger.WriteSerializerOf(sut.Default!).Should().BeSameAs(Fast);
    }

    [Fact]
    public void A_policy_without_a_serializer_is_the_instance_that_was_configured()
    {
        var policy = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        var defaults = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(2) };

        var sut = new DefaultCachePolicyFactory(new Dictionary<string, CachePolicy> { ["orders"] = policy }, null, default, Registry());
        var withDefault = new DefaultCachePolicyFactory(null, defaults, default, Registry());

        sut.Resolve("orders").Should().BeSameAs(policy);
        withDefault.Default.Should().BeSameAs(defaults);
    }

    [Fact]
    public void The_container_validates_names_when_the_factory_is_resolved()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            _ => { },
            options => options.Policies = new Dictionary<string, CachePolicy> { ["orders"] = Writing("fast") });
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<ICachePolicyFactory>();

        act.Should().Throw<OptionsValidationException>().WithMessage("*orders*").WithMessage("*fast*");
    }

    [Fact]
    public void The_container_resolves_a_registered_serializer_into_the_policy()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            builder => builder.AddValueSerializer("fast", _ => Fast),
            options => options.Policies = new Dictionary<string, CachePolicy> { ["orders"] = Writing("fast") });
        using var provider = services.BuildServiceProvider();

        CachePolicyMerger.WriteSerializerOf(provider.GetRequiredService<ICachePolicyFactory>().Resolve("orders")!).Should().BeSameAs(Fast);
    }

    [Fact]
    public void Distributed_cache_resolves_under_a_default_that_writes_another_format()
    {
        using var provider = DistributedCacheServices(defaultPolicy: Writing("fast"));

        provider.GetRequiredService<IDistributedCache>().Should().NotBeNull();
    }

    [Fact]
    public void Distributed_cache_policy_is_json_whatever_its_named_policy_inherits()
    {
        var policies = new Dictionary<string, CachePolicy> { ["sessions"] = new() { LocalExpiration = TimeSpan.FromMinutes(1) } };
        using var provider = DistributedCacheServices(defaultPolicy: Writing("fast"), policies: policies, policyName: "sessions");

        var adapter = provider.GetRequiredService<IDistributedCache>().Should().BeOfType<UiPathDistributedCache>().Subject;

        CachePolicyMerger.WriteSerializerOf(provider.GetRequiredService<ICachePolicyFactory>().Resolve("sessions")!).Should().BeSameAs(Fast);
        JsonOnly(adapter.Policy!);
        adapter.Policy!.LocalExpiration.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Distributed_cache_policy_is_json_when_the_named_policy_writes_another_format()
    {
        var policies = new Dictionary<string, CachePolicy> { ["sessions"] = Writing("fast") };
        using var provider = DistributedCacheServices(policies: policies, policyName: "sessions");

        var adapter = provider.GetRequiredService<IDistributedCache>().Should().BeOfType<UiPathDistributedCache>().Subject;

        JsonOnly(adapter.Policy!);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Distributed_cache_resolves_with_an_empty_or_json_serializer_policy(bool json)
    {
        var serializer = json ? Writing("json") : new CachePolicy { Serializer = new SerializerPolicy() };
        using var provider = DistributedCacheServices(defaultPolicy: serializer);

        provider.GetRequiredService<IDistributedCache>().Should().NotBeNull();
    }

    [Fact]
    public void The_adapters_policy_factory_hands_out_json_only_policies()
    {
        var inner = new DefaultCachePolicyFactory(
            new Dictionary<string, CachePolicy> { ["sessions"] = new() { LocalExpiration = TimeSpan.FromMinutes(1) } },
            Writing("fast"),
            default,
            Registry());

        var sut = new AdapterPolicyFactory(inner);

        JsonOnly(sut.Default!);
        JsonOnly(sut.Resolve("sessions")!);
        sut.Resolve("sessions")!.LocalExpiration.Should().Be(TimeSpan.FromMinutes(1));
        CachePolicyMerger.WriteSerializerOf(inner.Default!).Should().BeSameAs(Fast);
    }

    [Fact]
    public void Distributed_cache_without_a_serializer_still_resolves()
    {
        var services = new ServiceCollection();
        services.AddCaching(
            builder =>
            {
                builder.AddMemory(_ => { });
                builder.AddDistributedCache(KnownCacheProviderNames.InMemory);
            },
            options => options.DefaultCachePolicy = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) });
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDistributedCache>().Should().NotBeNull();
    }

    private static ServiceProvider DistributedCacheServices(
        CachePolicy? defaultPolicy = null, Dictionary<string, CachePolicy>? policies = null, string? policyName = null)
    {
        var services = new ServiceCollection();
        services.AddCaching(
            builder =>
            {
                builder.AddMemory(_ => { });
                builder.AddValueSerializer("fast", _ => Fast);
                builder.AddDistributedCache(KnownCacheProviderNames.InMemory, o => o.PolicyName = policyName);
            },
            options =>
            {
                options.DefaultCachePolicy = defaultPolicy;
                options.Policies = policies ?? [];
            });
        return services.BuildServiceProvider();
    }

    private static void JsonOnly(CachePolicy policy)
    {
        policy.Serializer!.Write.Should().Be("json");
        policy.Serializer.Read.Should().Equal("json");
        policy.Serializer.MigrateOnRead.Should().BeFalse();
        CachePolicyMerger.WriteSerializerOf(policy).Should().BeNull();
    }

    private static CachePolicy Reading(params string[] read) => new() { Serializer = new SerializerPolicy { Read = read } };

    private static CachePolicy Writing(string? write) => new() { Serializer = new SerializerPolicy { Write = write } };

    private static ValueSerializerRegistry Registry() => new([("fast", Fast, false)]);
}
