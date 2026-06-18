namespace UiPath.Caching.Tests;

public class CacheOfTPolicyResolutionTests(ITestContextAccessor testContextAccessor)
{
    [Fact]
    public void PolicyName_defaults_to_typeof_T_FullName_when_name_omitted()
    {
        var policyFactory = Substitute.For<ICachePolicyFactory>();
        policyFactory.Resolve(default!).ReturnsForAnyArgs(new CachePolicy());
        var cacheFactory = BuildCacheFactory(policyFactory);

        _ = new Cache<MyService>(cacheFactory);

        policyFactory.Received(1).Resolve(typeof(MyService).FullName!);
    }

    [Fact]
    public void Explicit_name_overrides_type_name()
    {
        var policyFactory = Substitute.For<ICachePolicyFactory>();
        policyFactory.Resolve(default!).ReturnsForAnyArgs(new CachePolicy());
        var cacheFactory = BuildCacheFactory(policyFactory);

        _ = new Cache<MyService>(cacheFactory, policyName: "tenant-settings");

        policyFactory.Received(1).Resolve("tenant-settings");
    }

    [Fact]
    public void Policy_resolved_at_construction_not_per_call()
    {
        var policyFactory = Substitute.For<ICachePolicyFactory>();
        policyFactory.Resolve(default!).ReturnsForAnyArgs(new CachePolicy());
        var inner = Substitute.For<ICache>();
        var cacheFactory = BuildCacheFactory(policyFactory, inner);
        var sut = new Cache<MyService>(cacheFactory);
        policyFactory.ClearReceivedCalls();

        var token = testContextAccessor.Current.CancellationToken;
        _ = sut.GetAsync("k", token);
        _ = sut.SetAsync("k", new MyService(), token);
        _ = sut.ContainsAsync("k", token);

        policyFactory.DidNotReceiveWithAnyArgs().Resolve(default!);
    }

    [Fact]
    public void Null_policy_factory_leaves_Policy_null_so_ICache_can_resolve_via_its_own_factory_default()
    {
        var cacheFactory = BuildCacheFactory(policyFactory: null);

        var sut = new Cache<MyService>(cacheFactory);

        sut.Policy.Should().BeNull();
    }

    [Fact]
    public void Resolved_policy_is_captured_as_Policy_property()
    {
        var resolved = new CachePolicy { LocalExpiration = TimeSpan.FromMinutes(1) };
        var policyFactory = Substitute.For<ICachePolicyFactory>();
        policyFactory.Resolve(default!).ReturnsForAnyArgs(resolved);
        var cacheFactory = BuildCacheFactory(policyFactory);

        var sut = new Cache<MyService>(cacheFactory);

        sut.Policy.Should().BeSameAs(resolved);
    }

    private static ICacheFactory BuildCacheFactory(ICachePolicyFactory? policyFactory, ICache? inner = null)
    {
        var cacheFactory = Substitute.For<ICacheFactory>();
        cacheFactory.PolicyFactory.Returns(policyFactory!);
        cacheFactory.CreateCache(default).ReturnsForAnyArgs(inner ?? Substitute.For<ICache>());
        return cacheFactory;
    }

    public class MyService { }
}
