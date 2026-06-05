namespace UiPath.Platform.Caching.Tests;

public class SetCacheOfTPolicyResolutionTests
{
    [Fact]
    public void PolicyName_defaults_to_typeof_T_FullName_when_name_omitted()
    {
        var policyFactory = Substitute.For<ICachePolicyFactory>();
        policyFactory.Resolve(default!).ReturnsForAnyArgs(new CachePolicy());

        _ = new SetCache<MyService>(Substitute.For<ISetCache>(), policyFactory: policyFactory);

        policyFactory.Received(1).Resolve(typeof(MyService).FullName!);
    }

    [Fact]
    public void Explicit_name_overrides_type_name()
    {
        var policyFactory = Substitute.For<ICachePolicyFactory>();
        policyFactory.Resolve(default!).ReturnsForAnyArgs(new CachePolicy());

        _ = new SetCache<MyService>(Substitute.For<ISetCache>(), policyFactory: policyFactory, policyName: "user-orgs");

        policyFactory.Received(1).Resolve("user-orgs");
    }

    [Fact]
    public void Null_policy_factory_leaves_Policy_null()
    {
        var sut = new SetCache<MyService>(Substitute.For<ISetCache>());

        sut.Policy.Should().BeNull();
    }

    public class MyService { }
}
