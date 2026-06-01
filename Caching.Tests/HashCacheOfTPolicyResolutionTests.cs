namespace UiPath.Platform.Caching.Tests;

public class HashCacheOfTPolicyResolutionTests
{
    [Fact]
    public void PolicyName_defaults_to_typeof_T_FullName_when_name_omitted()
    {
        var policyFactory = Substitute.For<ICachePolicyFactory>();
        policyFactory.Resolve(default!).ReturnsForAnyArgs(CachePolicy.Empty);
        var cacheFactory = BuildCacheFactory(policyFactory);

        _ = new HashCache<MyService>(cacheFactory);

        policyFactory.Received(1).Resolve(typeof(MyService).FullName!);
    }

    [Fact]
    public void Explicit_name_overrides_type_name()
    {
        var policyFactory = Substitute.For<ICachePolicyFactory>();
        policyFactory.Resolve(default!).ReturnsForAnyArgs(CachePolicy.Empty);
        var cacheFactory = BuildCacheFactory(policyFactory);

        _ = new HashCache<MyService>(cacheFactory, policyName: "user-orgs");

        policyFactory.Received(1).Resolve("user-orgs");
    }

    [Fact]
    public void Null_policy_factory_leaves_Policy_null_so_IHashCache_can_resolve_via_its_own_factory_default()
    {
        var cacheFactory = BuildCacheFactory(policyFactory: null);

        var sut = new HashCache<MyService>(cacheFactory);

        sut.Policy.Should().BeNull();
    }

    private static ICacheFactory BuildCacheFactory(ICachePolicyFactory? policyFactory)
    {
        var cacheFactory = Substitute.For<ICacheFactory>();
        cacheFactory.PolicyFactory.Returns(policyFactory!);
        cacheFactory.CreateHashCache(default).ReturnsForAnyArgs(Substitute.For<IHashCache>());
        return cacheFactory;
    }

    public class MyService { }
}
