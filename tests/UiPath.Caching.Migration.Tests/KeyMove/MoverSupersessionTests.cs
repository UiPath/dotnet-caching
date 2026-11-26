using StackExchange.Redis;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// CAP -4, a live supersession marker, goes to the handler seam with the marker and the identity of the unit, and the handler's answer decides.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverSupersessionTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_seam_receives_the_exact_marker_the_group_the_cache_the_key_and_the_identity_of_the_unit()
    {
        var handler = new ScriptedHandler(SupersessionAction.Skip);
        CapFakingDatabase? fake = null;
        await using var kit = await MoverKit.CreateAsync(fixture, supersession: handler, interceptDb: db => CapFakingDatabase.Over(db, out fake));
        await kit.SeedAsync("k");
        fake!.Supersede(1, "marker-é-1");

        var result = await kit.Target.MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, Ct);

        result.SkipReason.Should().Be("superseded");
        var seen = handler.Seen.Should().ContainSingle().Subject;
        seen.Marker.Should().Be((RedisValue)"marker-é-1");
        (seen.Group, seen.CacheId, seen.Key, seen.Identity).Should().Be((kit.Kit.Group, kit.Kit.Cid, "k", kit.Kit.Id));
    }

    [Fact]
    public async Task The_default_skips_and_counts_the_key_and_the_next_pass_moves_it()
    {
        CapFakingDatabase? fake = null;
        await using var kit = await MoverKit.CreateAsync(fixture, interceptDb: db => CapFakingDatabase.Over(db, out fake));
        await kit.SeedAsync("k");
        fake!.Supersede(1);
        var pause = TimeSpan.FromMilliseconds(300);
        var host = kit.Host(new MoverOptions { PollInterval = TimeSpan.FromMilliseconds(40), PassPause = pause, MinScanInterval = TimeSpan.Zero });

        host.Start();
        try
        {
            await kit.WaitPassAttemptAsync(host);
            var counters = await kit.ProgressAsync();
            counters.Should().Contain("skipped:superseded", 1);
            var unit = (await kit.UnitIdsAsync()).Single();
            await kit.Db.KeyExpireAsync(kit.Kit.Names.UnitPace(kit.Kit.Group, unit, "fwd", MoverKit.MoveId), DateTimeOffset.UnixEpoch.UtcDateTime);
            kit.Kit.Time.Advance(pause);
            await kit.WaitForPassAsync(host);
            (await kit.ProgressAsync()).Should().BeEquivalentTo(new Dictionary<string, long> { ["skipped:superseded"] = 1, ["moved"] = 1 });
        }
        finally
        {
            await host.StopAsync();
        }

        (await kit.Kit.GetAsync(kit.Kit.N("k"))).Should().Be("v-k");
        (await kit.Kit.ExistsAsync(kit.Kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task Retry_tries_the_key_once_more_in_the_same_batch_and_no_further()
    {
        var handler = new ScriptedHandler(SupersessionAction.Retry);
        CapFakingDatabase? fake = null;
        await using var kit = await MoverKit.CreateAsync(fixture, supersession: handler, interceptDb: db => CapFakingDatabase.Over(db, out fake));
        await kit.SeedAsync("once", "always");
        fake!.Supersede(1);

        (await kit.Target.MoveAsync("once", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, Ct)).Moved.Should().BeTrue();
        handler.Seen.Should().HaveCount(1);

        fake.Supersede(100);
        (await kit.Target.MoveAsync("always", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, Ct)).SkipReason.Should().Be("superseded");
        handler.Seen.Should().HaveCount(3, "one retry for the key that stayed superseded, and then it is left for the next pass");
    }

    [Fact]
    public async Task Rerun_with_a_changed_identity_ends_the_key_and_the_batch_and_never_promotes_under_the_new_identity()
    {
        CapFakingDatabase? fake = null;
        ScriptedHandler? handler = null;
        MoverKit? holder = null;
        handler = new ScriptedHandler(SupersessionAction.Rerun) { Before = async () => await holder!.Kit.MoveGateAsync() };
        await using var kit = await MoverKit.CreateAsync(fixture, supersession: handler, interceptDb: db => CapFakingDatabase.Over(db, out fake));
        holder = kit;
        await kit.SeedAsync("k");
        var before = kit.Kit.Id;
        fake!.Supersede(1);

        var result = await kit.Target.MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, Ct);

        result.Moved.Should().BeFalse();
        result.SkipReason.Should().Be("identity");
        handler.Seen.Should().ContainSingle().Which.Identity.Should().Be(before);
        kit.Kit.Id.Should().NotBe(before);
        (await kit.Kit.ExistsAsync(kit.Kit.N("k"))).Should().BeFalse("the identity now in force is not the runner's");
        (await kit.Kit.ExistsAsync(kit.Kit.L("k"))).Should().BeTrue();
    }

    [Fact]
    public async Task Rerun_when_the_identity_did_not_change_leaves_the_key_as_superseded()
    {
        var handler = new ScriptedHandler(SupersessionAction.Rerun);
        CapFakingDatabase? fake = null;
        await using var kit = await MoverKit.CreateAsync(fixture, supersession: handler, interceptDb: db => CapFakingDatabase.Over(db, out fake));
        await kit.SeedAsync("k");
        fake!.Supersede(5);

        (await kit.Target.MoveAsync("k", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, Ct)).SkipReason.Should().Be("superseded");

        handler.Seen.Should().ContainSingle();
        (await kit.Kit.ExistsAsync(kit.Kit.N("k"))).Should().BeFalse();
    }

    private sealed class ScriptedHandler(SupersessionAction action) : ISupersessionHandler
    {
        private readonly List<SupersededKey> _seen = [];

        public IReadOnlyList<SupersededKey> Seen
        {
            get
            {
                lock (_seen)
                {
                    return [.. _seen];
                }
            }
        }

        public Func<Task>? Before { get; init; }

        public async Task<SupersessionAction> OnSupersededAsync(SupersededKey context, CancellationToken cancellationToken)
        {
            lock (_seen)
            {
                _seen.Add(context);
            }

            if (Before is not null)
            {
                await Before();
            }

            return action;
        }
    }
}
