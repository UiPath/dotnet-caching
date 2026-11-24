using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A cursor is resumed only under a server whose run id was verified; without one the unit waits.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverCursorTests(RedisFixture fixture)
{
    private static readonly MoverOptions Options = MoverKit.Quick with { MinScanInterval = TimeSpan.Zero };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(InfoFault.Fail)]
    [InlineData(InfoFault.NoRunId)]
    public async Task A_stored_cursor_is_never_resumed_while_the_run_id_of_the_server_cannot_be_verified(InfoFault fault)
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 12).Select(i => $"k{i}").ToArray();
        await kit.SeedAsync(keys);
        ScanRecordingServer? scans = null;
        var units = new ProxyUnits(kit.Units, u => u with { Primary = ScanRecordingServer.Over(u.Primary, out scans) });
        var unit = (await units.ListAsync(Ct)).Single();
        scans!.Info = fault;

        // What a run left behind while the server named no run id: a position bound to the endpoint alone, which every later process of the server matches.
        var patterns = MoverUnitRunner.HashOf(kit.Space.Patterns(kit.Kit.Group, MoveSide.Legacy));
        var cursorKey = kit.Kit.Names.UnitCursor(kit.Kit.Group, unit.Id, "fwd", MoverKit.MoveId);
        var stored = new ScanCursor(0, "3", kit.Kit.Id, unit.Primary.EndPoint + "#", patterns, 0);
        await kit.Db.StringSetAsync(cursorKey, stored.Text);
        var host = kit.Host(Options, units: units);
        host.Start();
        try
        {
            await MoverKit.WaitAsync(() => Task.FromResult(kit.Kit.Plane.Alerts.Any(a => a.Code == "mover_unit_unverified")));
            scans.Cursors.Should().BeEmpty("no SCAN is sent with a position of a server that cannot be told from its restart");
            ((string?)await kit.Db.StringGetAsync(cursorKey)).Should().Be(stored.Text);
            (await kit.PassedAsync()).Should().BeFalse();

            // The run id comes back: the position of an unverified run is not resumed either, and the pass starts from zero.
            scans.Info = InfoFault.None;
            await MoverKit.WaitAsync(kit.PassedAsync);
        }
        finally
        {
            await host.StopAsync();
        }

        scans.Cursors[0].Should().Be("0");
        foreach (var key in keys)
        {
            (await kit.Kit.GetAsync(kit.Kit.N(key))).Should().Be("v-" + key);
        }
    }
}
