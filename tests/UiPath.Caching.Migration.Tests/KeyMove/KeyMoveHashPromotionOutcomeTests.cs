using System.Reflection;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// How a promotion ends when its source, its connection, its identity or its codec lets it down: what the read serves, what Redis holds afterwards, and what is counted.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashPromotionOutcomeTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Original = new() { ["a"] = "1", ["b"] = "2", ["c"] = "3" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_read_whose_capture_fails_on_the_connection_is_unavailable_and_promotes_nothing()
    {
        FreezableDatabase? freezer = null;
        await using var kit = await StagedAsync(interceptDb: db => FreezableDatabase.Over(db, out freezer));
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            freezer!.Frozen = step == SchedulePointStep.Capture;
            return Task.CompletedTask;
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Unavailable);
        await AssertNotPromotedAsync(kit);
    }

    [Fact]
    public async Task A_read_that_loses_the_connection_before_the_probe_of_L_is_unavailable()
    {
        await using var kit = await StagedAsync();
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.ReadDestinationOnMaster)
            {
                kit.Kit.Connected = false;
            }

            return Task.CompletedTask;
        };

        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unavailable);
        kit.Kit.Connected = true;
        await AssertNotPromotedAsync(kit);
    }

    [Fact]
    public async Task A_read_that_cannot_enter_serves_the_union_uncached_and_an_ensure_is_unavailable()
    {
        await using var kit = await StagedAsync();
        kit.Kit.Plane.RefuseEntry = true;

        var read = await kit.Client.ReadAsync("k", null, Ct);
        var ensured = await kit.Client.Promotion.PromoteAsync("k", HashPromotionFlavour.Ensure, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Uncached.Should().BeTrue();
        read.Fields!.Keys.Should().BeEquivalentTo(Original.Keys);
        ensured.Status.Should().Be(HashPromotionStatus.Unavailable);
        await AssertNotPromotedAsync(kit);
    }

    [Fact]
    public async Task A_promotion_refused_by_an_identity_nobody_can_join_serves_the_union_uncached()
    {
        await using var kit = await StagedAsync();
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Capture)
            {
                await kit.Kit.AdvanceRecordsAsync("k", kit.Kit.Ep + 1, "elsewhere");
            }
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Uncached.Should().BeTrue();
        await AssertNotPromotedAsync(kit);
    }

    [Fact]
    public async Task A_promotion_that_meets_a_new_identity_runs_again_under_it_and_promotes()
    {
        await using var kit = await StagedAsync();
        using var meter = new MeterCollector(kit.Kit.Cid);
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Capture && nth == 1)
            {
                await kit.Kit.MoveGateAsync();
                kit.Kit.ApplyRules("S3");
            }
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        meter.Count("move_identity_rerun").Should().Be(1);
    }

    [Fact]
    public async Task A_read_that_finds_the_state_unmanaged_after_a_new_identity_is_answered_by_the_plain_path()
    {
        await using var kit = await StagedAsync();
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Capture && nth == 1)
            {
                await kit.Kit.MoveGateAsync();
                kit.Kit.Plane.Rules = GroupRules.Unaffected;
            }
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Absent, "the plain stub holds nothing");
        kit.Plain.Reads.Should().Be(1);
        await AssertNotPromotedAsync(kit);
    }

    [Fact]
    public async Task A_source_that_is_not_a_hash_is_unreadable_and_counted_and_stays()
    {
        await using var kit = await StagedAsync(seed: false);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Db.StringSetAsync(kit.L("k"), "not a hash");

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Unreadable);
        meter.Count("move_unreadable_source").Should().Be(1);
        ((string?)await kit.Db.StringGetAsync(kit.L("k"))).Should().Be("not a hash");
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_source_that_cannot_be_read_on_the_connection_is_unavailable_and_stays()
    {
        FreezableDatabase? freezer = null;
        await using var kit = await StagedAsync(interceptDb: db => FreezableDatabase.Over(db, out freezer));
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            freezer!.Frozen = step == SchedulePointStep.ReadSource;
            return Task.CompletedTask;
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Unavailable);
        await AssertNotPromotedAsync(kit);
    }

    [Fact]
    public async Task A_source_whose_codec_cannot_decode_it_is_unreadable_in_every_state_that_reads_it()
    {
        await using var kit = await StagedAsync(inPlace: new FaultyCodec(decode: true));
        using var meter = new MeterCollector(kit.Kit.Cid);

        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unreadable);
        meter.Count("move_unreadable_source").Should().Be(1);
        await kit.Kit.SetStateAsync("S1", nPop: 0);

        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unreadable);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original);
    }

    [Fact]
    public async Task A_hash_the_native_codec_cannot_encode_is_served_uncached_and_stays_in_L()
    {
        await using var kit = await StagedAsync(native: new FaultyCodec(encode: true));

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Uncached.Should().BeTrue();
        read.Fields!.Keys.Should().BeEquivalentTo(Original.Keys);
        await AssertNotPromotedAsync(kit);
    }

    [Fact]
    public async Task A_promotion_whose_snapshot_came_late_after_the_capture_serves_the_hash_uncached_and_counts_it()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var kit = await StagedAsync(clock: clock);
        using var meter = new MeterCollector(kit.Kit.Cid);
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.ReadSource)
            {
                clock.Advance(TimeSpan.FromMinutes(5));
            }

            return Task.CompletedTask;
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Uncached.Should().BeTrue();
        meter.Count("move_capture_slow").Should().Be(1);
        await AssertNotPromotedAsync(kit);
    }

    [Fact]
    public async Task A_promotion_that_sees_the_connection_change_is_abandoned_and_the_hash_is_served_uncached()
    {
        await using var kit = await StagedAsync();
        using var meter = new MeterCollector(kit.Kit.Cid);
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.ReadSource)
            {
                kit.Kit.ConnectionEvent();
            }

            return Task.CompletedTask;
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Uncached.Should().BeTrue();
        meter.Count("move_promotion_abandoned").Should().Be(1);
        await AssertNotPromotedAsync(kit);
    }

    [Fact]
    public async Task A_staging_key_that_does_not_hold_the_whole_hash_is_dropped_and_nothing_is_published()
    {
        await using var kit = await StagedAsync(interceptDb: db => InterceptingProxy.Wrap(db, ShortStaging));

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Uncached.Should().BeTrue();
        await AssertNotPromotedAsync(kit);
        (await StagingKeysAsync(kit)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_publish_that_fails_on_the_connection_drops_the_staging_key_and_serves_the_hash_uncached()
    {
        FreezableDatabase? freezer = null;
        await using var kit = await StagedAsync(interceptDb: db => FreezableDatabase.Over(db, out freezer));
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            freezer!.Frozen = step == SchedulePointStep.Publish;
            return Task.CompletedTask;
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        freezer!.Frozen = false;
        read.Kind.Should().Be(ReadOutcome.Found);
        read.Uncached.Should().BeTrue();
        await AssertNotPromotedAsync(kit);
    }

    [Fact]
    public async Task A_destination_that_appears_before_the_publish_is_served_and_L_is_left_alone()
    {
        await using var kit = await StagedAsync();
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Publish)
            {
                await kit.SetAsync(kit.N("k"), new Dictionary<string, string> { ["z"] = "26" });
            }
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Fields!.Keys.Should().BeEquivalentTo("z");
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["z"] = "26" });
    }

    [Fact]
    public async Task A_lost_publish_uses_the_same_side_for_both_field_selections()
    {
        await using var kit = await StagedAsync(seed: false, clock: new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var source = new Dictionary<string, string> { ["broken"] = "source value", ["good"] = "source good" };
        var winner = new Dictionary<string, string> { ["broken"] = "junk:native", ["good"] = "native good" };
        await kit.SetAsync(kit.L("k"), source);
        kit.Kit.Schedule.Hook = (step, _) => step == SchedulePointStep.Publish ? kit.SetAsync(kit.N("k"), winner) : Task.CompletedTask;

        var narrow = await kit.Client.ReadAsync("k", ["broken"], Ct);
        await kit.Db.KeyDeleteAsync(kit.N("k"));
        var wide = await kit.Client.ReadAsync("k", ["broken", "good"], Ct);
        await kit.Db.KeyDeleteAsync(kit.N("k"));
        var whole = await kit.Client.ReadAsync("k", null, Ct);

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(winner);
        narrow.Fields.Should().BeEquivalentTo(whole.Only(["broken"]).Fields);
        wide.Fields.Should().BeEquivalentTo(whole.Fields);
        wide.Fields!["good"].Should().Be(new Item("source good"));
    }

    [Fact]
    public async Task An_unreadable_source_is_counted_only_when_recovery_cannot_answer()
    {
        await using var kit = await StagedAsync(seed: false);
        using var meter = new MeterCollector(kit.Kit.Cid);
        var source = new Dictionary<string, string> { ["good"] = "value", ["broken"] = "junk:x" };
        await kit.SetAsync(kit.L("k"), source);

        var recovered = await kit.Client.ReadAsync("k", ["good", "broken"], Ct);

        recovered.Fields.Should().BeEquivalentTo(KeyMoveHashKit.Items(("good", "value")));
        meter.Count("move_unreadable_source").Should().Be(0);
        (await kit.Client.ReadAsync("k", ["broken"], Ct)).Kind.Should().Be(ReadOutcome.Unreadable);
        meter.Count("move_unreadable_source").Should().Be(1);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(source);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_publish_refused_by_a_new_identity_runs_again_when_the_identity_can_be_joined_and_serves_uncached_when_not(bool joinable)
    {
        await using var kit = await StagedAsync();
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step != SchedulePointStep.Publish || nth != 1)
            {
                return;
            }

            if (joinable)
            {
                await kit.Kit.MoveGateAsync();
                kit.Kit.ApplyRules("S3");
            }
            else
            {
                await kit.Kit.AdvanceRecordsAsync("k", kit.Kit.Ep + 1, "elsewhere");
            }
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Uncached.Should().Be(!joinable);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().Be(joinable);
    }

    [Fact]
    public async Task A_publish_made_after_the_life_of_the_source_is_refused_and_counted()
    {
        await using var kit = await StagedAsync(seed: false);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMilliseconds(1500));
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Publish)
            {
                await Task.Delay(1800, Ct);
            }
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        meter.Count("move_promotion_expiring").Should().Be(1);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_promotion_whose_delete_of_L_fails_on_the_connection_leaves_both_copies_and_is_not_complete()
    {
        FreezableDatabase? freezer = null;
        await using var kit = await StagedAsync(interceptDb: db => FreezableDatabase.Over(db, out freezer));
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            freezer!.Frozen = step == SchedulePointStep.CompareDelete;
            return Task.CompletedTask;
        };

        var promoted = await kit.Client.Promotion.PromoteAsync("k", HashPromotionFlavour.Ensure, Ct);

        freezer!.Frozen = false;
        promoted.Status.Should().NotBe(HashPromotionStatus.Complete);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original, "the delete of L did not reach the server");
    }

    [Fact]
    public async Task A_promotion_that_throws_outside_a_command_is_unavailable()
    {
        await using var kit = await StagedAsync();
        kit.Kit.Schedule.Hook = (step, _) => step == SchedulePointStep.Capture ? throw new InvalidOperationException("the schedule broke") : Task.CompletedTask;

        var promoted = await kit.Client.Promotion.PromoteAsync("k", HashPromotionFlavour.Ensure, Ct);

        promoted.Status.Should().Be(HashPromotionStatus.Unavailable);
        await AssertNotPromotedAsync(kit);
    }

    [Fact]
    public async Task A_merge_whose_promotion_meets_a_new_identity_runs_again_and_a_merge_with_no_reruns_left_is_refused()
    {
        await using var kit = await StagedAsync();
        using var meter = new MeterCollector(kit.Kit.Cid);
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Capture && nth == 1)
            {
                await kit.Kit.MoveGateAsync();
                kit.Kit.ApplyRules("S3");
            }
        };

        var result = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), new HashWriteOptions(HashCacheSetOption.HashReplace, null), Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string>(Original) { ["d"] = "4" });
        meter.Count("move_identity_rerun").Should().BeGreaterThanOrEqualTo(1);

        await using var strict = await StagedAsync(options: new KeyMoveOptions { MaxReruns = 0 });
        strict.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Capture)
            {
                await strict.Kit.MoveGateAsync();
                strict.Kit.ApplyRules("S3");
            }
        };

        await FluentActions.Awaiting(async () => await strict.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), new HashWriteOptions(HashCacheSetOption.HashReplace, null), Ct)).Should().ThrowAsync<CacheUnavailableException>();
        (await strict.FieldsAsync(strict.L("k"))).Should().BeEquivalentTo(Original);
    }

    [Fact]
    public async Task A_merge_that_cannot_probe_L_or_promote_it_is_not_sent()
    {
        await using var offline = await StagedAsync();
        offline.Kit.Connected = false;
        (await offline.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), new HashWriteOptions(HashCacheSetOption.HashReplace, null), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        await using var unavailable = await StagedAsync();
        unavailable.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.ProbeSource)
            {
                unavailable.Kit.Plane.Held = null;
            }

            return Task.CompletedTask;
        };

        (await unavailable.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), new HashWriteOptions(HashCacheSetOption.HashReplace, null), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
    }

    [Fact]
    public async Task A_probe_that_fails_on_the_connection_is_not_a_presence_answer()
    {
        FreezableDatabase? freezer = null;
        await using var kit = await StagedAsync(interceptDb: db => FreezableDatabase.Over(db, out freezer));
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            freezer!.Frozen = step == SchedulePointStep.ProbeSource;
            return Task.CompletedTask;
        };

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), new HashWriteOptions(HashCacheSetOption.HashReplace, null), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        freezer!.Frozen = false;
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original);
    }

    private static object? ShortStaging(MethodInfo method, object?[] args) =>
        method.Name == nameof(IDatabase.HashLengthAsync) && args[0] is RedisKey key && key.ToString().Contains(":stg:", StringComparison.Ordinal) ? Task.FromResult(0L) : InterceptingProxy.Proceed;

    private static async Task<List<RedisKey>> StagingKeysAsync(KeyMoveHashKit kit)
    {
        var found = new List<RedisKey>();
        foreach (var server in kit.Kit.Mux.GetServers().Where(s => !s.IsReplica))
        {
            found.AddRange(server.Keys(pattern: $"*{kit.Kit.Unique}*:stg:*"));
        }

        await Task.CompletedTask;
        return found;
    }

    private static async Task AssertNotPromotedAsync(KeyMoveHashKit kit)
    {
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original, "L is the copy until a promotion publishes");
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("nothing was published");
    }

    private async Task<KeyMoveHashKit> StagedAsync(bool seed = true, Func<IDatabase, IDatabase>? interceptDb = null, IKeyMoveHashCodec<Item>? inPlace = null, IKeyMoveHashCodec<Item>? native = null, TimeProvider? clock = null, KeyMoveOptions? options = null)
    {
        var kit = await KeyMoveHashKit.CreateAsync(fixture, options: options, inPlace: inPlace, native: native, clock: clock, interceptDb: interceptDb);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        if (seed)
        {
            await kit.SetAsync(kit.L("k"), Original);
        }

        return kit;
    }

    // A codec that fails where a test asks it to, and otherwise is the codec of the tests.
    private sealed class FaultyCodec(bool encode = false, bool decode = false) : IKeyMoveHashCodec<Item>
    {
        private readonly ItemHashCodec _inner = new(false, false);

        public bool CachesNulls => _inner.CachesNulls;

        public bool CarriesMetadata => _inner.CarriesMetadata;

        public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields(IDictionary<string, Item?> fields, IDictionary<string, string?>? metadata) =>
            encode ? throw new InvalidOperationException("The fields cannot be encoded.") : _inner.EncodeFields(fields, metadata);

        public DecodedHash<Item> DecodeFields(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored, bool capped = true) =>
            decode ? throw new InvalidOperationException("The fields cannot be decoded.") : _inner.DecodeFields(stored, capped);
    }
}
