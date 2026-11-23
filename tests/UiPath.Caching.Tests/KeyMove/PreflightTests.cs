using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class PreflightTests(RedisContainerFixture fixture)
{
    private static readonly KeyMoveStateOptions Attested = new() { ExpectedReplicasPerMaster = 0, EvictionPolicyAttested = true };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_server_that_runs_the_protocol_passes_in_script_and_fallback_mode(bool useScripts)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts, Attested);

        var result = await kit.Preflight.CheckAsync(evictionApproved: false, CancellationToken.None);

        result.Ok.Should().BeTrue(string.Join(", ", result.Failures.Select(f => f.Code)));
    }

    [Fact]
    public async Task A_connection_that_cannot_read_the_eviction_policy_needs_an_attestation_or_an_approval()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);

        var unknown = await kit.Preflight.CheckAsync(evictionApproved: false, CancellationToken.None);
        var approved = await kit.Preflight.CheckAsync(evictionApproved: true, CancellationToken.None);

        unknown.Failures.Select(f => f.Code).Should().Equal("eviction_policy_unknown");
        approved.Ok.Should().BeTrue();
    }

    [Theory]
    [InlineData("noeviction", true)]
    [InlineData("volatile-lru", true)]
    [InlineData("volatile-ttl", true)]
    [InlineData("allkeys-lru", false)]
    [InlineData("allkeys-random", false)]
    public async Task Only_noeviction_and_the_volatile_policies_keep_a_control_record(string policy, bool passes)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        kit.Topology.Probe = _ => new MasterProbe(0, policy);

        var result = await kit.Preflight.CheckAsync(evictionApproved: false, CancellationToken.None);

        result.Ok.Should().Be(passes);
        if (!passes)
        {
            result.Failures.Should().ContainSingle().Which.Code.Should().Be("eviction_policy");
            (await kit.Preflight.CheckAsync(evictionApproved: true, CancellationToken.None)).Ok.Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_master_with_fewer_replicas_connected_than_configured_is_unhealthy()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, new KeyMoveStateOptions { ExpectedReplicasPerMaster = 2, EvictionPolicyAttested = true });
        kit.Topology.Probe = _ => new MasterProbe(1, "noeviction");

        var result = await kit.Preflight.CheckAsync(evictionApproved: false, CancellationToken.None);

        result.Failures.Should().ContainSingle().Which.Code.Should().Be("replica_unhealthy");
        kit.Topology.Probe = _ => new MasterProbe(2, "noeviction");
        (await kit.Preflight.CheckAsync(evictionApproved: false, CancellationToken.None)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_standalone_connection_with_no_configured_replica_count_fails_instead_of_guessing()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true, new KeyMoveStateOptions { EvictionPolicyAttested = true });

        var result = await kit.Preflight.CheckAsync(evictionApproved: false, CancellationToken.None);

        result.Failures.Should().ContainSingle().Which.Code.Should().Be("replicas_unknown");
    }

    [Fact]
    public async Task Every_master_of_a_cluster_is_checked()
    {
        await using var kit = await StateKit.ClusterAsync(useScripts: true, new KeyMoveStateOptions { ExpectedReplicasPerMaster = 0, EvictionPolicyAttested = true });
        var seen = new List<string>();
        kit.Topology.Probe = master =>
        {
            lock (seen)
            {
                seen.Add(master.ToString()!);
            }

            return null;
        };

        var result = await kit.Preflight.CheckAsync(evictionApproved: false, CancellationToken.None);

        result.Ok.Should().BeTrue(string.Join(", ", result.Failures.Select(f => f.Detail)));
        seen.Distinct().Should().HaveCount(3);
    }
}
