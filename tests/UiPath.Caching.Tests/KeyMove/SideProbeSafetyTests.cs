using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The scan capability on every primary, the scan budget on a fake clock, and probe keys that always expire.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class SideProbeSafetyTests(RedisContainerFixture fixture)
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.";
    private static readonly EndPoint A = new DnsEndPoint("a", 6379);
    private static readonly EndPoint B = new DnsEndPoint("b", 6379);

    [Fact]
    public async Task A_primary_that_denies_scan_fails_the_gate_though_an_earlier_one_ran_out_of_budget()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var denied = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM User u has no permissions to run the 'scan' command");
        var side = new FakeSide(new FakePrimary(A, ScanEndless: true, OnScan: () => clock.Advance(TimeSpan.FromSeconds(10))), new FakePrimary(B, Scan: denied));

        var caps = await SideProbe.ProbeAsync(side.Database, "side:", "side:*", TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken, timeProvider: clock);

        caps.Scan.Should().Be(ScanMode.None, "B was never shown to answer SCAN, so the side must not pass as Incomplete");
        caps.Denied.Should().Contain($"SCAN@{B}");
        caps.Failures("src").Should().Contain("src: the scan probe failed");
        side.Scanned.Should().Equal([A, B], "each primary got its one capability step before any traversal");
    }

    [Fact]
    public async Task The_scan_budget_follows_the_injected_clock()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var side = new FakeSide(new FakePrimary(A, ScanEndless: true, OnScan: () => clock.Advance(TimeSpan.FromSeconds(10))));
        var ct = TestContext.Current.CancellationToken;

        var bounded = await SideProbe.ProbeAsync(side.Database, "side:", "side:*", TimeSpan.FromSeconds(25), cancellationToken: ct, timeProvider: clock);

        bounded.Scan.Should().Be(ScanMode.Incomplete);
        side.Scanned.Should().HaveCount(4, "one capability step, then steps at 10 s, 20 s and 30 s of a 25 s budget that began after the first");

        var finished = await SideProbe.ProbeAsync(new FakeSide(new FakePrimary(A)).Database, "side:", "side:*", TimeSpan.FromSeconds(25), cancellationToken: ct, timeProvider: clock);
        finished.Scan.Should().Be(ScanMode.PerShard);
    }

    [Fact]
    public async Task A_script_that_fails_halfway_with_del_denied_leaves_keys_that_expire_and_reports_the_cleanup()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var admin = ConfigurationOptions.Parse(fixture.ConnectionString);
        admin.AllowAdmin = true;
        await using var adminMux = await ConnectionMultiplexer.ConnectAsync(admin);
        var server = adminMux.GetServer(adminMux.GetEndPoints()[0]);
        var user = $"nodel{Guid.NewGuid():N}";
        await server.ExecuteAsync("ACL", "SETUSER", user, "on", ">nodelpw", "~*", "&*", "+@all", "-del", "-unlink", "-get");
        var db = adminMux.GetDatabase();
        var prefix = $"sidetest:nodel:{Guid.NewGuid():N}:";
        try
        {
            var limited = ConfigurationOptions.Parse(fixture.ConnectionString);
            limited.User = user;
            limited.Password = "nodelpw";
            await using var mux = await ConnectionMultiplexer.ConnectAsync(limited);

            var caps = await SideProbe.ProbeAsync(mux.GetDatabase(), prefix, $"{prefix}*", cancellationToken: TestContext.Current.CancellationToken);

            caps.Denied.Should().Contain(d => d.StartsWith("DEL@", StringComparison.Ordinal));
            caps.Uncleaned.Should().ContainSingle();
            caps.Notes("src").Should().Contain(n => n.Contains("could not delete its probe keys", StringComparison.Ordinal) && n.Contains(caps.Uncleaned[0].ToString()!, StringComparison.Ordinal));
            var leftovers = server.Keys(pattern: $"{prefix}*").ToArray();
            leftovers.Should().NotBeEmpty("the script created its key before GET was denied");
            foreach (var key in leftovers)
            {
                (await db.KeyTimeToLiveAsync(key)).Should().NotBeNull($"{key} was created with its expiry");
            }
        }
        finally
        {
            await db.KeyDeleteAsync([.. server.Keys(pattern: $"{prefix}*")]);
            await server.ExecuteAsync("ACL", "DELUSER", user);
        }
    }
}
