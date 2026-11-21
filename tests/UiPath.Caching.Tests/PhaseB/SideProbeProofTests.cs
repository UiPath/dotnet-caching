using System.Net;
using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

/// <summary>The probe of what a WAIT proof needs on every master: CLIENT ID, INFO and a connection with allowAdmin.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class SideProbeProofTests(RedisContainerFixture fixture)
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.";
    private static readonly EndPoint A = new DnsEndPoint("a", 6379);
    private static readonly EndPoint B = new DnsEndPoint("b", 6379);

    [Fact]
    public async Task A_master_that_denies_client_id_fails_the_gate()
    {
        var side = new FakeSide(new FakePrimary(A), new FakePrimary(B, Client: Noperm("client|id")));

        var caps = await SideProbe.ProbeAsync(side.Database, "side:", "side:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Failures("src").Should().ContainSingle().Which.Should().Be($"src: {B} denies CLIENT ID, so a WAIT cannot be proven");
        caps.Denied.Should().Contain($"CLIENT ID@{B}");
    }

    [Fact]
    public async Task A_master_that_denies_info_fails_the_gate()
    {
        var side = new FakeSide(new FakePrimary(A, Info: Noperm("info")), new FakePrimary(B));

        var caps = await SideProbe.ProbeAsync(side.Database, "side:", "side:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Failures("dst").Should().ContainSingle().Which.Should().Be($"dst: {A} denies INFO, so a WAIT cannot be proven");
        caps.Denied.Should().Contain($"INFO@{A}");
    }

    [Fact]
    public async Task A_connection_without_allow_admin_fails_the_gate_once_for_the_side()
    {
        var admin = new RedisCommandException("This operation is not available unless admin mode is enabled: INFO");
        var side = new FakeSide(new FakePrimary(A, Info: admin, Client: admin), new FakePrimary(B, Info: admin));

        var caps = await SideProbe.ProbeAsync(side.Database, "side:", "side:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Failures("src").Should().ContainSingle().Which.Should().Be("src: the move connection needs allowAdmin");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_busy_info_or_client_id_is_inconclusive_and_reports_no_denial(bool info)
    {
        var busy = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "BUSY Redis is busy running a script. You can only call SCRIPT KILL or SHUTDOWN NOSAVE.");
        var side = new FakeSide(info ? new FakePrimary(A, Info: busy) : new FakePrimary(A, Client: busy), new FakePrimary(B));

        var caps = await SideProbe.ProbeAsync(side.Database, "side:", "side:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Failures("src").Should().BeEmpty();
        caps.Failures("dst").Should().BeEmpty();
        caps.Denied.Should().NotContain(d => d.StartsWith("INFO@", StringComparison.Ordinal) || d.StartsWith("CLIENT ID@", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_master_that_answers_both_adds_no_failure()
    {
        var side = new FakeSide(new FakePrimary(A), new FakePrimary(B));

        var caps = await SideProbe.ProbeAsync(side.Database, "side:", "side:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Failures("src").Should().BeEmpty();
    }

    [Theory]
    [InlineData("-client|id", "denies CLIENT ID")]
    [InlineData("-info", "denies INFO")]
    public async Task A_user_denied_client_id_or_info_fails_the_gate_on_a_real_server(string denial, string expected)
    {
        Assert.SkipUnless(fixture.Enabled && fixture.SupportsAcl, Skip);
        var options = ConfigurationOptions.Parse(fixture.ConnectionString);
        options.AllowAdmin = true;
        await using var adminMux = await ConnectionMultiplexer.ConnectAsync(options);
        var server = adminMux.GetServer(adminMux.GetEndPoints()[0]);
        var user = $"noproof{Guid.NewGuid():N}";
        // A subcommand rule needs Redis 7; before it, denying the whole command still denies CLIENT ID.
        var rule = denial == "-client|id" && fixture.ServerVersion < new Version(7, 0) ? "-client" : denial;
        await server.ExecuteAsync("ACL", "SETUSER", user, "on", ">noproofpw", "~*", "&*", "+@all", rule);
        try
        {
            var limited = ConfigurationOptions.Parse(fixture.ConnectionString);
            limited.User = user;
            limited.Password = "noproofpw";
            limited.AllowAdmin = true;
            await using var mux = await ConnectionMultiplexer.ConnectAsync(limited);

            var caps = await SideProbe.ProbeAsync(mux.GetDatabase(), $"sidetest:{user}:", $"sidetest:{user}:*", cancellationToken: TestContext.Current.CancellationToken);

            caps.Failures("src").Should().Contain(f => f.Contains(expected, StringComparison.Ordinal) && f.Contains("a WAIT cannot be proven", StringComparison.Ordinal));
        }
        finally
        {
            await server.ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    [Fact]
    public async Task A_real_connection_without_allow_admin_fails_the_gate()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);

        var caps = await SideProbe.ProbeAsync(mux.GetDatabase(), "sidetest:noadmin:", "sidetest:noadmin:*", cancellationToken: TestContext.Current.CancellationToken);

        caps.Failures("src").Should().Contain("src: the move connection needs allowAdmin");
    }

    private static RedisServerException Noperm(string command) => new(RedisErrorKind.None, CommandFlags.None, $"NOPERM User u has no permissions to run the '{command}' command");
}
