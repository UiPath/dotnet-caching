using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UiPath.Caching.Broadcast;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A hash cache of a key move group whose targets the router itself fills: the mover moves its keys, the drain check passes and no key is left without a target.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class HashMoverRouterTests(RedisFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Life = TimeSpan.FromHours(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_hash_cache_the_router_binds_is_moved_by_the_mover_of_the_router_through_the_states_to_native_and_the_drain_completes()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        var keys = new[] { "k1", "k2", "k3" };
        var targets = new MoveTargets();
        using var router = Bind(kit, targets);
        IHashCache<Item> cache = router;
        await kit.Kit.PrepareAsync(keys);

        await kit.Kit.SetStateAsync("S1", nPop: 0);
        foreach (var key in keys)
        {
            (await cache.SetAsync(key, Items(("a", key + "a"), ("b", key + "b")), TimeSpan.MaxValue, Ct)).Should().BeTrue();
        }

        await kit.Kit.SetStateAsync("S3");
        await RunPassAsync(Host(kit, targets));

        foreach (var key in keys)
        {
            (await kit.Kit.ExistsAsync(kit.L(key))).Should().BeFalse("the mover took the hash out of L");
            (await kit.FieldsAsync(kit.N(key))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = key + "a", ["b"] = key + "b" });
        }

        (await ProgressAsync(kit)).Should().Contain("moved", keys.Length);
        kit.Kit.Plane.Alerts.Should().NotContain(a => a.Code == "mover_no_target");
        var passes = Passes(kit, Space(kit));
        (await passes.FinishedAfterAckAsync(kit.Kit.Group, "fwd", Ct)).Should().BeTrue("every unit finished a pass and the group has a hash cache");

        await kit.Kit.SetStateAsync("S5", swept: true);
        foreach (var key in keys)
        {
            (await cache.GetAsync(key, Ct)).Keys.Should().BeEquivalentTo("a", "b");
        }
    }

    // What a target does with a key of the other type is decided by the scan: a pattern of hash caches is scanned for hashes and every other for strings, so none reaches a target it cannot promote.
    [Fact]
    public async Task In_a_group_of_a_scalar_and_a_hash_cache_each_target_sees_only_keys_of_its_type_and_strays_of_other_types_are_left_alone_and_do_not_block_the_drain()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        var targets = new MoveTargets();
        using var router = Bind(kit, targets);
        var scalarId = kit.Kit.Cid + "s";
        var scalarParts = new KeyMoveClientParts<Item>(kit.Kit.Group, scalarId, false, kit.Kit.L, kit.Kit.N, ItemCodec.Instance, ItemCodec.Instance, kit.Kit.Plain, kit.Kit.Plane, kit.Kit.Clock, () => TimeSpan.FromSeconds(30));
        targets.Add(kit.Kit.Group, new KeyMover<Item>(scalarParts));
        kit.Kit.Others.Add(scalarId);
        await kit.Kit.PrepareAsync("h:1", "h:2", "s:1", "s:2");
        await kit.Kit.SetStateAsync("S3");
        kit.Kit.Plane.Rules = kit.Kit.Plane.Rules with { Managed = new HashSet<string> { kit.Kit.Cid, scalarId } };
        await kit.SetAsync(kit.L("h:1"), new Dictionary<string, string> { ["a"] = "1" }, Life);
        await kit.SetAsync(kit.L("h:2"), new Dictionary<string, string> { ["a"] = "2" }, Life);
        await kit.Db.StringSetAsync(kit.L("s:1"), "first", TimeSpan.FromHours(1));
        await kit.Db.StringSetAsync(kit.L("s:2"), "second", TimeSpan.FromHours(1));

        // Strays: a string and a list under the pattern of the hash cache, a hash and a list under the pattern of the scalar cache.
        await kit.Db.StringSetAsync(kit.L("h:stray-string"), "text", Life);
        await kit.Db.ListRightPushAsync(kit.L("h:stray-list"), ["x", "y"]);
        await kit.SetAsync(kit.L("s:stray-hash"), new Dictionary<string, string> { ["f"] = "v" }, Life);
        await kit.Db.ListRightPushAsync(kit.L("s:stray-list"), ["x", "y"]);
        await RunPassAsync(Host(kit, targets, mixed: true));

        (await kit.Kit.ExistsAsync(kit.L("h:1"))).Should().BeFalse();
        (await kit.FieldsAsync(kit.N("h:2"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "2" });
        (await kit.Kit.ExistsAsync(kit.L("s:1"))).Should().BeFalse();
        (await kit.Kit.GetAsync(kit.N("s:2"))).Should().Be("second");
        (await kit.Db.StringGetAsync(kit.L("h:stray-string"))).ToString().Should().Be("text");
        (await kit.Db.ListLengthAsync(kit.L("h:stray-list"))).Should().Be(2);
        (await kit.FieldsAsync(kit.L("s:stray-hash"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["f"] = "v" });
        (await kit.Db.ListLengthAsync(kit.L("s:stray-list"))).Should().Be(2);
        foreach (var stray in new[] { "h:stray-string", "h:stray-list", "s:stray-hash", "s:stray-list" })
        {
            (await kit.Kit.ExistsAsync(kit.N(stray))).Should().BeFalse($"{stray} was never promoted");
        }

        (await ProgressAsync(kit)).Should().BeEquivalentTo(new Dictionary<string, long> { ["moved"] = 4 }, "a stray is not scanned, so it is neither moved nor skipped");
        kit.Kit.Plane.Alerts.Should().NotContain(a => a.Code == "mover_no_target");
        var passes = Passes(kit, Space(kit, mixed: true));
        (await passes.FinishedAfterAckAsync(kit.Kit.Group, "fwd", Ct)).Should().BeTrue("a key of a type no cache stores is not the mover's and never blocks the drain");
    }

    // The final stage deletes what L still holds, so a drain must not confirm while a type of a shared pattern has not been scanned: strings are scanned first, hashes second.
    [Fact]
    public async Task A_pattern_shared_by_a_scalar_and_a_hash_cache_is_scanned_for_both_types_and_the_drain_confirms_only_after_the_hash_scan()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        var routed = new MoveTargets();
        using var router = Bind(kit, routed);
        var scalarId = kit.Kit.Cid + "s";
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hashTarget = new CountingTarget(routed.Of(kit.Kit.Group)[kit.Kit.Cid])
        {
            Before = async (_, _) =>
            {
                reached.TrySetResult();
                await release.Task;
                return null;
            },
        };
        var scalarParts = new KeyMoveClientParts<Item>(kit.Kit.Group, scalarId, false, kit.Kit.L, kit.Kit.N, ItemCodec.Instance, ItemCodec.Instance, kit.Kit.Plain, kit.Kit.Plane, kit.Kit.Clock, () => TimeSpan.FromSeconds(30));
        var targets = new Dictionary<string, IMoveTarget> { [kit.Kit.Cid] = hashTarget, [scalarId] = new KeyMover<Item>(scalarParts) };
        kit.Kit.Others.Add(scalarId);
        await kit.Kit.PrepareAsync("h:1", "h:2", "s:1", "s:2");
        await kit.Kit.SetStateAsync("S3");
        kit.Kit.Plane.Rules = kit.Kit.Plane.Rules with { Managed = new HashSet<string> { kit.Kit.Cid, scalarId } };
        await kit.SetAsync(kit.L("h:1"), new Dictionary<string, string> { ["a"] = "1" }, Life);
        await kit.SetAsync(kit.L("h:2"), new Dictionary<string, string> { ["a"] = "2" }, Life);
        await kit.Db.StringSetAsync(kit.L("s:1"), "first", TimeSpan.FromHours(1));
        await kit.Db.StringSetAsync(kit.L("s:2"), "second", TimeSpan.FromHours(1));
        var passes = Passes(kit, Space(kit, mixed: true, shared: true));
        var (host, pass) = Host(kit, targets, mixed: true, shared: true);

        host.Start();
        try
        {
            await reached.Task.WaitAsync(Ct);

            // The string scan is done and the hash scan has begun.
            (await kit.Kit.ExistsAsync(kit.L("s:1"))).Should().BeFalse();
            (await kit.Kit.ExistsAsync(kit.L("h:1"))).Should().BeTrue("the hash scan has not moved anything yet");
            (await passes.FinishedAfterAckAsync(kit.Kit.Group, "fwd", Ct)).Should().BeFalse("the hashes of the shared pattern are still in L");
            release.SetResult();
            await pass.WaitAsync(Ct);
        }
        finally
        {
            release.TrySetResult();
            await host.StopAsync();
        }

        (await kit.FieldsAsync(kit.N("h:2"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "2" });
        (await kit.Kit.GetAsync(kit.N("s:2"))).Should().Be("second");
        (await kit.Kit.ExistsAsync(kit.L("h:1"))).Should().BeFalse();
        hashTarget.TotalMoved.Should().Be(2, "only the hashes reach the hash cache");
        (await ProgressAsync(kit)).Should().BeEquivalentTo(new Dictionary<string, long> { ["moved"] = 4 });
        (await passes.FinishedAfterAckAsync(kit.Kit.Group, "fwd", Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_hash_target_alerts_once_for_its_keys_and_leaves_the_pass_unclean()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await kit.Kit.PrepareAsync("first", "second");
        await kit.Kit.SetStateAsync("S3");
        var fields = new Dictionary<string, string> { ["a"] = "1" };
        await kit.SetAsync(kit.L("first"), fields, Life);
        await kit.SetAsync(kit.L("second"), fields, Life);
        var unit = (await new MultiplexerScanUnits(() => kit.Kit.Mux).ListAsync(Ct)).Single();
        var names = kit.Kit.Names;
        var lease = names.UnitLease(kit.Kit.Group, unit.Id);
        await RunPassAsync(Host(kit, new MoveTargets()));

        (await ProgressAsync(kit)).Should().Contain("skipped:no_target", 2);
        (await kit.Db.KeyExistsAsync(lease)).Should().BeFalse();
        (await Passes(kit, Space(kit)).FinishedAfterAckAsync(kit.Kit.Group, "fwd", Ct)).Should().BeFalse();
        kit.Kit.Plane.Alerts.Where(a => a.Code == "mover_no_target").Should().ContainSingle().Which.Detail.Should().Be(kit.Kit.Cid);
        (await kit.Kit.ExistsAsync(kit.L("first"))).Should().BeTrue();
        (await kit.Kit.ExistsAsync(kit.L("second"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_pass_record_without_a_scan_plan_blocks_the_drain_until_the_unit_passes_again()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var space = Space(kit);
        var group = kit.Kit.Group;
        var unit = (await new MultiplexerScanUnits(() => kit.Kit.Mux).ListAsync(Ct)).Single();
        var passKey = kit.Kit.Names.UnitPass(group, unit.Id, "fwd", MoverKit.MoveId);
        var runId = (await unit.Primary.InfoAsync("server")).SelectMany(section => section).Single(entry => entry.Key == "run_id").Value;
        var oldRecord = $"{kit.Kit.Id}|{MoverKit.MoveId}|1|0|{unit.Primary.EndPoint}#{runId}";
        await kit.Db.StringSetAsync(passKey, oldRecord);

        (await ConfirmDrainAsync(kit, space, noOrigin: false)).Passed.Should().BeFalse();

        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["field"] = "value" }, Life);
        var targets = new MoveTargets();
        using var cache = Bind(kit, targets);
        await RunPassAsync(Host(kit, targets));

        var completedAt = (Start + TimeSpan.FromSeconds(1)).ToUnixTimeMilliseconds();
        (await kit.Db.StringGetAsync(passKey)).ToString().Should().StartWith(string.Create(CultureInfo.InvariantCulture, $"{kit.Kit.Id}|{MoverKit.MoveId}|{completedAt}:"));
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await ConfirmDrainAsync(kit, space, noOrigin: false)).Passed.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_empty_pattern_advances_and_the_last_pattern_can_start_or_resume_at_cursor_zero(bool resume)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        const string key = "h:last";
        await kit.Kit.PrepareAsync(key);
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L(key), new Dictionary<string, string> { ["field"] = "value" }, Life);
        var space = new RegisteredKeySpace([Cache(kit, kit.Kit.Cid + "s", "s:", false), Cache(kit, kit.Kit.Cid, "h:", true)], kit.N(string.Empty));
        var group = kit.Kit.Group;
        var plan = MoveScan.HashOf(MoveScan.For(space, group));
        var real = new MultiplexerScanUnits(() => kit.Kit.Mux);
        ScanRecordingServer? scanner = null;
        var units = new ProxyUnits(real, unit => unit with { Primary = ScanRecordingServer.Over(unit.Primary, out scanner) });
        var unit = (await units.ListAsync(Ct)).Single();
        scanner!.RunId = "runner";
        var calls = new List<string>();
        scanner.Reply = scan =>
        {
            var type = (string)scan[6];
            calls.Add(type);
            return ScanPage(type == "hash" ? [kit.L(key)] : []);
        };
        if (resume)
        {
            var position = new ScanCursor(1, "0", kit.Kit.Id, $"{unit.Primary.EndPoint}#runner", plan, 0);
            await kit.Db.StringSetAsync(kit.Kit.Names.UnitCursor(group, unit.Id, "fwd", MoverKit.MoveId), position.Text);
        }

        var targets = new MoveTargets();
        using var cache = Bind(kit, targets);
        await RunPassAsync(Host(kit.Kit, space, targets.Of(group), units, State(kit)));

        IEnumerable<string> expected = resume ? ["hash"] : ["string", "hash"];
        calls.Should().Equal(expected);
        (await kit.Kit.ExistsAsync(kit.L(key))).Should().BeFalse();
        (await kit.FieldsAsync(kit.N(key))).Should().Contain("field", "value");
        (await kit.Db.KeyExistsAsync(kit.Kit.Names.UnitCursor(group, unit.Id, "fwd", MoverKit.MoveId))).Should().BeFalse();
    }

    [Fact]
    public async Task A_leading_control_character_in_a_string_pattern_is_part_of_the_glob()
    {
        await using var kit = await MoverKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        const string key = "\u0001entry";
        await kit.SeedAsync(key);
        var group = kit.Kit.Group;
        var connector = kit.Kit.L(string.Empty);
        var native = kit.Kit.N(string.Empty);
        var cache = new KeyMoveCache(kit.Kit.Cid, group, false, false, false, null, "\u0001*", k => k, k => k, native + "*", kit.Kit.N, k => k[native.Length..]);
        var space = new RegisteredKeySpace([cache], native);
        var targets = new Dictionary<string, IMoveTarget> { [kit.Kit.Cid] = kit.Target };

        await RunPassAsync(Host(kit.Kit, space, targets, kit.Units, kit.State(), connector));

        (await kit.Kit.GetAsync(kit.Kit.N(key))).Should().Be("v-" + key);
        MoveScan.HashOf([new MoveScan("\u0001*", KeyMoveKind.String)]).Should().NotBe(MoveScan.HashOf([new MoveScan("*", KeyMoveKind.Hash)]));
    }

    [Fact]
    public async Task A_hash_dry_run_scans_its_registered_type_and_leaves_the_hash_in_place()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await kit.SetAsync(kit.L("hash"), new Dictionary<string, string> { ["field"] = "value" });
        await kit.Db.StringSetAsync(kit.L("stray"), "value");
        var targets = new MoveTargets();
        using var cache = Bind(kit, targets);
        var group = kit.Kit.Group;
        var runner = new DryRunner(group, kit.Kit.Plane, Space(kit), targets.Of(group), new MultiplexerScanUnits(() => kit.Kit.Mux), string.Empty, NoLimit.Instance, kit.Kit.Clock);

        var report = await runner.RunAsync(Ct);

        report.Scanned.Should().Be(1);
        report.Caches[kit.Kit.Cid].Movable.Should().Be(1);
        report.EstimatedNativeBytes.Should().Be(10);
        (await kit.Kit.ExistsAsync(kit.L("hash"))).Should().BeTrue();
        (await kit.Kit.ExistsAsync(kit.N("hash"))).Should().BeFalse();
        await kit.Db.KeyDeleteAsync(kit.L("stray"));
        await kit.Db.KeyDeleteAsync(kit.L("hash"));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Confirming_inventory_resolves_hash_residue_by_the_same_origin_policy_as_strings(bool noOrigin, bool unreadable, bool passed)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: noOrigin, maxFields: 2, clock: new ManualTimeProvider(Start));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var fields = unreadable ? new Dictionary<string, string> { ["field"] = "junk:value" } : new Dictionary<string, string> { ["a"] = "value", ["b"] = "value", ["c"] = "value" };
        await kit.SetAsync(kit.L("k"), fields);
        var targets = new MoveTargets();
        var mover = new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion);
        targets.Add(kit.Kit.Group, mover);
        var space = new RegisteredKeySpace([Cache(kit, kit.Kit.Cid, string.Empty, true) with { NoOrigin = noOrigin }], kit.N(string.Empty));
        await RunPassAsync(Host(kit, targets));

        var drain = await ConfirmDrainAsync(kit, space, noOrigin);

        drain.Passed.Should().Be(passed);
        drain.Residue[kit.Kit.Cid].Should().Be(1);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_wrong_type_destination_preserves_the_legacy_hash_and_blocks_the_pass(bool replaceAfterPublish)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true, clock: new ManualTimeProvider(Start));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var fields = new Dictionary<string, string> { ["field"] = "value" };
        await kit.SetAsync(kit.L("k"), fields, Life);
        if (replaceAfterPublish)
        {
            kit.Kit.Schedule.Hook = async (step, _) =>
            {
                if (step == SchedulePointStep.ConfirmDestination)
                {
                    await kit.Db.StringSetAsync(kit.N("k"), "wrong type");
                }
            };
        }
        else
        {
            await kit.Db.StringSetAsync(kit.N("k"), "wrong type");
        }

        var targets = new MoveTargets();
        targets.Add(kit.Kit.Group, new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion));
        await RunPassAsync(Host(kit, targets));

        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(fields);
        (await kit.Db.StringGetAsync(kit.N("k"))).Should().Be((RedisValue)"wrong type");
        (await ProgressAsync(kit)).Should().Contain("skipped:destination_type", 1);
        (await Passes(kit, Space(kit)).FinishedAfterAckAsync(kit.Kit.Group, "fwd", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Inventory_and_mover_agree_when_owners_of_different_types_overlap_on_the_source()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["field"] = "value" }, Life);
        var scalar = Cache(kit, kit.Kit.Cid + "s", "s:", false) with { OwnsInPlace = key => key.StartsWith(kit.L(string.Empty), StringComparison.Ordinal) };
        var space = new RegisteredKeySpace([Cache(kit, kit.Kit.Cid, string.Empty, true), scalar], kit.N(string.Empty));
        var targets = new MoveTargets();
        targets.Add(kit.Kit.Group, new HashMover<Item>(kit.Client.Parts, kit.Client.Promotion));
        var group = kit.Kit.Group;

        var before = await ConfirmDrainAsync(kit, space, noOrigin: false);
        before.Should().NotBeNull("the string cache does not make the hash's ownership ambiguous");
        before.Residue[kit.Kit.Cid].Should().Be(1);

        await RunPassAsync(Host(kit.Kit, space, targets.Of(group), new MultiplexerScanUnits(() => kit.Kit.Mux), State(kit)));

        (await kit.FieldsAsync(kit.N("k"))).Should().Contain("field", "value");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await ConfirmDrainAsync(kit, space, noOrigin: false)).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task The_target_the_router_registers_for_a_hash_cache_is_a_hash_mover_over_the_promotion_of_its_client()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: new ManualTimeProvider(Start));
        var targets = new MoveTargets();
        using var cache = Bind(kit, targets);

        var target = targets.Of(kit.Kit.Group).Should().ContainKey(kit.Kit.Cid).WhoseValue.Should().BeOfType<HashMover<Item>>().Subject;

        target.CacheId.Should().Be(kit.Kit.Cid);
        cache.Library.Should().BeOfType<KeyMoveHashClient<Item>>();
    }

    private static RoutedHashCache<Item> Bind(KeyMoveHashKit kit, MoveTargets targets)
    {
        var core = new RouterCore(kit.Kit.Clock, _ => true, NullLogger.Instance)
        {
            SourceTimeout = TimeSpan.FromSeconds(30),
            StartBound = TimeSpan.FromSeconds(5),
            CleanupBound = TimeSpan.FromSeconds(10),
        };
        var registration = new RouterHashCacheRegistration<Item>
        {
            Name = kit.Kit.Cid,
            Group = kit.Kit.Group,
            Source = new FakeHashBackend([], new ManualTimeProvider(Start)),
            RedisKey = kit.L,
            KeyMove = new KeyMoveHashBinding<Item>(kit.Client.Parts.InPlace, kit.Client.Parts.Native, kit.L, kit.N, false, kit.Client.Parts.MaxHashFields),
        };
        var cache = new RoutedHashCache<Item>(core, registration);
        var source = new Uri($"urn:hash-mover-{Guid.NewGuid():N}");
        var library = new LibraryParts(
            new FakeMigrationCache(),
            new FakeConnection(),
            new FakeBus().For(source),
            new CacheEventFactory(Options.Create(new CacheOptions { SourceUri = source })),
            source,
            () => TimeSpan.FromSeconds(30),
            KeyMove: kit.Kit.Plane,
            Movers: targets,
            HashStore: new FakeMigrationHashCache());
        using var attachment = cache.Prepare(library);
        attachment.Commit();
        return cache;
    }

    private static (MoverHost Host, Task Pass) Host(KeyMoveHashKit kit, MoveTargets targets, bool mixed = false) =>
        Host(kit, targets.Of(kit.Kit.Group), mixed, false);

    private static (MoverHost Host, Task Pass) Host(KeyMoveHashKit kit, IReadOnlyDictionary<string, IMoveTarget> targets, bool mixed, bool shared) =>
        Host(kit.Kit, Space(kit, mixed, shared), targets, new MultiplexerScanUnits(() => kit.Kit.Mux), State(kit));

    private static (MoverHost Host, Task Pass) Host(KeyMoveKit kit, IGroupKeySpace space, IReadOnlyDictionary<string, IMoveTarget> targets, IScanUnits units, StateRecord state, string connector = "")
    {
        var pass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = MoverKit.Quick;
        var clock = new ManualTimeProvider(Start, due =>
        {
            // The runner starts its pass pause after it saves progress and releases the lease.
            if (due == options.PassPause)
            {
                pass.TrySetResult();
            }
        });
        clock.Advance(TimeSpan.FromSeconds(1));
        return (new MoverHost(kit.Group, kit.Plane, space, targets, units, _ => Task.FromResult<StateRecord?>(state), connector, clock, NoLimit.Instance, options), pass.Task);
    }

    private static RegisteredKeySpace Space(KeyMoveHashKit kit, bool mixed = false, bool shared = false)
    {
        var caches = mixed
            ? new[] { Cache(kit, kit.Kit.Cid, "h:", true, shared), Cache(kit, kit.Kit.Cid + "s", "s:", false, shared) }
            : [Cache(kit, kit.Kit.Cid, string.Empty, true)];
        return new RegisteredKeySpace(caches, kit.N("\u0001").Split('\u0001')[0]);
    }

    private static MoverPasses Passes(KeyMoveHashKit kit, IGroupKeySpace space) =>
        new(new MultiplexerScanUnits(() => kit.Kit.Mux), kit.Db, kit.Kit.Names, space, (_, _) => Task.FromResult<StateRecord?>(State(kit)));

    private static async Task RunPassAsync((MoverHost Host, Task Pass) run)
    {
        var (host, pass) = run;
        host.Start();
        try
        {
            await pass.WaitAsync(Ct);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static RedisResult ScanPage(string[] keys) =>
        RedisResult.Create([RedisResult.Create((RedisValue)"0"), RedisResult.Create(keys.Select(key => RedisResult.Create((RedisValue)key)).ToArray())]);

    private static async Task<DrainReport> ConfirmDrainAsync(KeyMoveHashKit kit, IGroupKeySpace space, bool noOrigin)
    {
        var clock = kit.Kit.Clock;
        var names = kit.Kit.Names;
        var state = State(kit);
        var redisKey = names.State(kit.Kit.Group);
        await kit.Db.HashSetAsync(redisKey,
        [
            new(StateFields.State, state.State),
            new(StateFields.Direction, state.Direction),
            new(StateFields.Ep, state.Ep),
            new(StateFields.Id, state.Id),
            new(StateFields.Caches, state.Caches),
            new(StateFields.AllAck, state.AllAck),
            new(StateFields.T3At, clock.GetUtcNow().ToUnixTimeMilliseconds()),
        ]);
        var topology = new MultiplexerTopology(kit.Kit.Mux, string.Empty, true, new Version(7, 4), 0);
        var store = new StateStore(kit.Db, names, kit.Kit.Scripts, new ControlSampler(kit.Db, names, topology), clock);
        var scanner = new MasterScanner(() => kit.Kit.Mux.GetServers(), topology, kit.Db.Database, string.Empty, clock, TimeSpan.Zero);
        var parts = kit.Client.Parts;
        var values = new KeyMoveValueClasses();
        values.AddHash(kit.Kit.Cid, new KeyMoveHashBinding<Item>(parts.InPlace, parts.Native, kit.L, kit.N, noOrigin, parts.MaxHashFields));
        var status = new InventoryMoverStatus(new KeyInventory(scanner, space, store, kit.Db, string.Empty, clock, values: values), clock, moverEvidence: Passes(kit, space).EvidenceAsync);
        try
        {
            return (await status.DrainAsync(kit.Kit.Group, (await store.ReadAsync(kit.Kit.Group, Ct))!, Ct))!;
        }
        finally
        {
            await kit.Db.KeyDeleteAsync(redisKey);
        }
    }

    // A cache whose keys all start with the prefix, in the key formats of the kit.
    private static KeyMoveCache Cache(KeyMoveHashKit kit, string id, string prefix, bool hash, bool shared = false)
    {
        // A shared pattern covers every key and the caches tell their keys apart by the caller key.
        var pattern = shared ? string.Empty : prefix;
        // The head and the tail of a native key, around the caller key.
        var native = kit.N("\u0001").Split('\u0001');
        var lPrefix = kit.L(string.Empty);
        return new KeyMoveCache(
            id,
            kit.Kit.Group,
            false,
            false,
            false,
            null,
            lPrefix + pattern + "*",
            kit.L,
            k => k.StartsWith(lPrefix + prefix, StringComparison.Ordinal) ? k[lPrefix.Length..] : null,
            native[0] + pattern + "*" + native[1],
            kit.N,
            k => k.StartsWith(native[0] + prefix, StringComparison.Ordinal) && k.EndsWith(native[1], StringComparison.Ordinal) ? k[native[0].Length..^native[1].Length] : null,
            OwnsInPlace: shared ? k => k.StartsWith(lPrefix + prefix, StringComparison.Ordinal) : null,
            OwnsNative: shared ? k => k.StartsWith(native[0] + prefix, StringComparison.Ordinal) : null,
            Kind: hash ? KeyMoveKind.Hash : KeyMoveKind.String);
    }

    private static StateRecord State(KeyMoveHashKit kit) => new("S3", "fwd", kit.Kit.Ep, kit.Kit.Id, true, 1, string.Empty, MoverKit.MoveId, kit.Kit.Cid, string.Empty, kit.Kit.Id, string.Empty, string.Empty, string.Empty);

    private static async Task<Dictionary<string, long>> ProgressAsync(KeyMoveHashKit kit) =>
        (await kit.Db.HashGetAllAsync(kit.Kit.Names.Progress(kit.Kit.Group))).ToDictionary(e => e.Name.ToString(), e => (long)e.Value);

    private static Dictionary<string, Item?> Items(params (string Field, string? Value)[] fields) => KeyMoveHashKit.Items(fields);
}
