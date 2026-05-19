using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Tests;

public class HashCacheOfTPolicyResolutionTests
{
    [Fact]
    public void PolicyName_defaults_to_typeof_T_FullName_when_name_omitted()
    {
        var policyFactory = Substitute.For<ICachePolicyFactory>();
        policyFactory.Resolve(default!).ReturnsForAnyArgs(CachePolicy.Empty);
        var inner = Substitute.For<IHashCache>();

        _ = new HashCache<MyService>(inner, cacheKeyStrategy: null, policyFactory: policyFactory);

        policyFactory.Received(1).Resolve(typeof(MyService).FullName!);
    }

    [Fact]
    public void Explicit_name_overrides_type_name()
    {
        var policyFactory = Substitute.For<ICachePolicyFactory>();
        policyFactory.Resolve(default!).ReturnsForAnyArgs(CachePolicy.Empty);
        var inner = Substitute.For<IHashCache>();

        _ = new HashCache<MyService>(inner, cacheKeyStrategy: null, policyFactory: policyFactory, name: "user-orgs");

        policyFactory.Received(1).Resolve("user-orgs");
    }

    [Fact]
    public void Null_policy_factory_leaves_Policy_null_so_IHashCache_can_resolve_via_its_own_factory_default()
    {
        var inner = Substitute.For<IHashCache>();

        var sut = new HashCache<MyService>(inner, cacheKeyStrategy: null, policyFactory: null);

        sut.Policy.Should().BeNull();
    }

    public class MyService { }
}
