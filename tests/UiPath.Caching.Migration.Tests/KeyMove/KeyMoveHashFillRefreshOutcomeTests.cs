using System.Reflection;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// How a hash fill, a refresh, a presence check and a read end when the state changes under them or the connection fails.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashFillRefreshOutcomeTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Original = new() { ["a"] = "1", ["b"] = "2", ["c"] = "3" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fill_planned_under_an_older_identity_fails_closed_when_the_rules_never_follow()
    {
        await using var kit = await StagedAsync(options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(5), MaxReruns = 2 });
        await kit.Kit.MoveGateAsync();

        var refused = kit.Client.FillAsync("k", Items(), Fill(), null, Ct).AsTask();
        await kit.Kit.Time.AdvanceNextTimerAsync(Ct);
        await kit.Kit.Time.AdvanceNextTimerAsync(Ct);
        await Assert.ThrowsAsync<CacheUnavailableException>(() => refused);

        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_fill_planned_under_an_older_identity_is_built_again_once_the_rules_follow()
    {
        await using var kit = await StagedAsync(options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(20), MaxReruns = 100 });
        await kit.Kit.MoveGateAsync();

        var fill = kit.Client.FillAsync("k", Items(), Fill(), null, Ct).AsTask();
        var admission = await kit.Kit.Time.NextTimerAsync(Ct);
        fill.IsCompleted.Should().BeFalse("the fill waits for the rules to follow the gate");
        kit.Kit.ApplyRules("S3");
        kit.Kit.Time.Advance(admission);

        (await fill).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
    }

    [Fact]
    public async Task A_fill_whose_life_has_passed_is_not_sent_and_a_fill_with_no_connection_is_not_sent()
    {
        await using var kit = await StagedAsync();

        (await kit.Client.FillAsync("k", Items(), Fill(kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(-1))), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        kit.Kit.Connected = false;
        (await kit.Client.FillAsync("k", Items(), Fill(), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        kit.Kit.Connected = true;
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_fill_the_codec_cannot_encode_is_not_sent_and_leaves_no_staging_key()
    {
        await using var kit = await StagedAsync(native: new FaultyEncoder());

        (await kit.Client.FillAsync("k", Items(), Fill(), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await StagingKeysAsync(kit)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_staging_key_that_does_not_hold_the_whole_hash_makes_the_fill_not_sent_and_is_dropped()
    {
        await using var kit = await StagedAsync(interceptDb: db => InterceptingProxy.Wrap(db, ShortStaging));

        (await kit.Client.FillAsync("k", Items(), Fill(), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await StagingKeysAsync(kit)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_fill_whose_script_fails_on_the_connection_is_Unknown()
    {
        FreezableDatabase? freezer = null;
        await using var kit = await StagedAsync(interceptDb: db => FreezableDatabase.Over(db, out freezer));
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            freezer!.Frozen = step == SchedulePointStep.Fill;
            return Task.CompletedTask;
        };

        var result = await kit.Client.FillAsync("k", Items(), Fill(), null, Ct);

        freezer!.Frozen = false;
        result.Outcome.Should().Be(WriteOutcome.Unknown);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_fill_whose_life_passes_while_it_is_staged_is_not_sent_by_the_client_or_by_the_script()
    {
        await using var kit = await StagedAsync();
        var clock = kit.Kit.Time;
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Fill)
            {
                clock.Advance(TimeSpan.FromMinutes(5));
            }

            return Task.CompletedTask;
        };

        (await kit.Client.FillAsync("k", Items(), Fill(clock.GetUtcNow().AddMinutes(1)), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent, "the client judges the deadline itself");

        await using var late = await StagedAsync();
        var behind = late.Kit.Time;
        behind.UtcOffset = TimeSpan.FromSeconds(-2);

        (await late.Client.FillAsync("k", Items(), Fill(behind.ToDateTimeOffset(TimeSpan.FromSeconds(1))), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent, "the script refuses a fill whose deadline has passed");
        (await late.Kit.ExistsAsync(late.N("k"))).Should().BeFalse();
        (await StagingKeysAsync(late)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_fill_refused_by_a_new_identity_runs_again_when_it_can_join_and_goes_to_the_plain_path_when_the_state_is_unmanaged(bool unmanaged)
    {
        await using var kit = await StagedAsync();
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Fill && nth == 1)
            {
                await kit.Kit.MoveGateAsync();
                if (unmanaged)
                {
                    kit.Kit.Plane.Rules = GroupRules.Unaffected;
                }
                else
                {
                    kit.Kit.ApplyRules("S3");
                }
            }
        };

        var result = await kit.Client.FillAsync("k", Items(), Fill(), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed);
        kit.Plain.Fills.Should().Be(unmanaged ? 1 : 0);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().Be(!unmanaged);
    }

    [Fact]
    public async Task A_fill_with_no_reruns_left_after_a_new_identity_is_not_sent()
    {
        await using var kit = await StagedAsync(options: new KeyMoveOptions { MaxReruns = 0 });
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Fill)
            {
                await kit.Kit.MoveGateAsync();
                kit.Kit.ApplyRules("S3");
            }
        };

        (await kit.Client.FillAsync("k", Items(), Fill(), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await StagingKeysAsync(kit)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_refresh_planned_under_an_older_identity_waits_for_the_rules_and_fails_closed_when_they_never_follow()
    {
        await using var strict = await StagedAsync(options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(5), MaxReruns = 2 });
        await strict.Kit.MoveGateAsync();
        var refused = strict.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromHours(1)), Ct).AsTask();
        await strict.Kit.Time.AdvanceNextTimerAsync(Ct);
        await strict.Kit.Time.AdvanceNextTimerAsync(Ct);
        await Assert.ThrowsAsync<CacheUnavailableException>(() => refused);

        await using var kit = await StagedAsync(options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(20), MaxReruns = 100 });
        await kit.SetAsync(kit.N("k"), Original, TimeSpan.FromMinutes(1));
        await kit.Kit.MoveGateAsync();
        var refresh = kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromHours(1)), Ct).AsTask();
        var admission = await kit.Kit.Time.NextTimerAsync(Ct);
        refresh.IsCompleted.Should().BeFalse();
        kit.Kit.ApplyRules("S3");
        kit.Kit.Time.Advance(admission);

        (await refresh).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(50));
    }

    [Fact]
    public async Task A_refresh_with_no_connection_is_not_sent_and_one_that_fails_on_the_connection_is_Unknown()
    {
        FreezableDatabase? freezer = null;
        await using var kit = await StagedAsync(interceptDb: db => FreezableDatabase.Over(db, out freezer));
        await kit.SetAsync(kit.N("k"), Original, TimeSpan.FromMinutes(1));
        kit.Kit.Connected = false;
        (await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromHours(1)), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        kit.Kit.Connected = true;
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            freezer!.Frozen = step == SchedulePointStep.HashExpire;
            return Task.CompletedTask;
        };

        var result = await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromHours(1)), Ct);

        freezer!.Frozen = false;
        result.Outcome.Should().Be(WriteOutcome.Unknown);
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeLessThan(TimeSpan.FromMinutes(2), "the extension never reached the server");
    }

    [Theory]
    [InlineData("again")]
    [InlineData("exhausted")]
    [InlineData("unmanaged")]
    public async Task A_refresh_refused_by_a_new_identity_runs_again_ends_not_sent_when_the_reruns_are_used_up_and_goes_plain_when_unmanaged(string ending)
    {
        await using var kit = await StagedAsync(options: new KeyMoveOptions { MaxReruns = ending == "exhausted" ? 0 : 3 });
        await kit.SetAsync(kit.N("k"), Original, TimeSpan.FromMinutes(1));
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.HashExpire && nth == 1)
            {
                await kit.Kit.MoveGateAsync();
                if (ending == "unmanaged")
                {
                    kit.Kit.Plane.Rules = GroupRules.Unaffected;
                }
                else
                {
                    kit.Kit.ApplyRules("S3");
                }
            }
        };

        var result = await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromHours(1)), Ct);

        result.Outcome.Should().Be(ending == "exhausted" ? WriteOutcome.NotSent : WriteOutcome.Committed);
        kit.Plain.Refreshes.Should().Be(ending == "unmanaged" ? 1 : 0);
        var left = (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value;
        if (ending == "again")
        {
            left.Should().BeGreaterThan(TimeSpan.FromMinutes(50));
        }
        else
        {
            left.Should().BeLessThan(TimeSpan.FromMinutes(2), "the extension was never applied");
        }
    }

    [Fact]
    public async Task A_forward_refresh_that_cannot_probe_L_is_not_sent()
    {
        await using var kit = await StagedAsync();
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.HashExpire)
            {
                kit.Kit.Connected = false;
            }

            return Task.CompletedTask;
        };

        (await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromHours(1)), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        kit.Kit.Connected = true;
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k"))).Should().BeNull("L is never extended");
    }

    [Fact]
    public async Task A_forward_refresh_whose_promotion_meets_a_new_identity_runs_again_and_extends_N()
    {
        await using var kit = await StagedAsync();
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Capture && nth == 1)
            {
                await kit.Kit.MoveGateAsync();
                kit.Kit.ApplyRules("S3");
            }
        };

        var result = await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromHours(1)), Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(50));
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_pending_retry_that_runs_out_of_life_is_counted_alerted_and_dropped()
    {
        await using var kit = await StagedAsync();
        var clock = kit.Kit.Time;
        using var meter = new MeterCollector(kit.Kit.Cid);
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.HashWrite)
            {
                kit.Kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null), Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        var path = (IHashPathClient<Item>)kit.Client;
        path.InFlightKeys.Should().Contain("k");

        clock.Advance(TimeSpan.FromHours(1));
        await kit.Client.Retries.RunDueAsync(all: true);

        meter.Count("move_retry_exhausted").Should().Be(1);
        kit.Kit.Plane.Alerts.Should().ContainSingle(a => a.Code == "retry_exhausted");
        kit.Client.Retries.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_presence_check_or_an_expiry_in_a_moving_state_is_unavailable_with_no_connection_and_when_the_connection_fails()
    {
        FreezableDatabase? freezer = null;
        await using var kit = await StagedAsync(interceptDb: db => FreezableDatabase.Over(db, out freezer));
        kit.Kit.Connected = false;
        await FluentActions.Awaiting(async () => await kit.Client.ContainsAsync("k", Ct)).Should().ThrowAsync<CacheUnavailableException>();
        kit.Kit.Connected = true;
        freezer!.Frozen = true;

        await FluentActions.Awaiting(async () => await kit.Client.ExpireTimeAsync("k", Ct)).Should().ThrowAsync<CacheUnavailableException>();
        freezer.Frozen = false;
        (await kit.Client.ContainsAsync("k", Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_NoOrigin_read_of_an_absent_hash_in_S1_asks_the_record_and_falls_back_to_the_union_when_the_record_cannot_be_read()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", nPop: 0);
        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Absent);
        kit.Kit.Schedule.Steps.Should().Contain(SchedulePointStep.ControlCheck);

        await using var unrecorded = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await unrecorded.Kit.SetStateAsync("S1", nPop: 0);
        await unrecorded.SetAsync(unrecorded.N("k"), Original);
        (await unrecorded.Client.ReadAsync("k", null, Ct)).Fields!.Keys.Should().BeEquivalentTo(Original.Keys, "a key with no record is read as the union reads it");

        FreezableDatabase? freezer = null;
        await using var broken = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true, interceptDb: db => FreezableDatabase.Over(db, out freezer));
        await broken.Kit.PrepareAsync("k");
        await broken.Kit.SetStateAsync("S1", nPop: 0);
        await broken.SetAsync(broken.N("k"), Original);
        broken.Kit.Schedule.Hook = (step, _) =>
        {
            freezer!.Frozen = step == SchedulePointStep.ControlCheck;
            return Task.CompletedTask;
        };
        var union = await broken.Client.ReadAsync("k", null, Ct);
        union.Fields!.Keys.Should().BeEquivalentTo(Original.Keys, "a record that cannot be read sends the read to the union");
    }

    [Fact]
    public async Task A_hash_read_with_no_connection_is_unavailable_and_one_of_a_key_that_is_not_a_hash_is_unreadable()
    {
        await using var kit = await StagedAsync();
        await kit.Kit.SetStateAsync("S1", nPop: 0);
        kit.Kit.Connected = false;
        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unavailable);
        kit.Kit.Connected = true;
        await kit.Db.KeyDeleteAsync(kit.L("k"));
        await kit.Db.StringSetAsync(kit.L("k"), "text");

        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unreadable);
        (await kit.Client.Promotion.PromoteAsync("k", HashPromotionFlavour.Ensure, Ct)).Status.Should().Be(HashPromotionStatus.Replan);
    }

    private static object? ShortStaging(MethodInfo method, object?[] args) =>
        method.Name == nameof(IDatabase.HashLengthAsync) && args[0] is RedisKey key && key.ToString().Contains(":stg:", StringComparison.Ordinal) ? Task.FromResult(0L) : InterceptingProxy.Proceed;

    private static Dictionary<string, Item?> Items() => KeyMoveHashKit.Items([.. Original.Select(e => (e.Key, (string?)e.Value))]);

    private static HashWriteOptions Fill(DateTimeOffset? deadline = null) => new(HashCacheSetOption.KeyReplace, deadline);

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

    private async Task<KeyMoveHashKit> StagedAsync(Func<IDatabase, IDatabase>? interceptDb = null, IKeyMoveHashCodec<Item>? native = null, TimeProvider? clock = null, KeyMoveOptions? options = null)
    {
        var kit = await KeyMoveHashKit.CreateAsync(fixture, options: options, native: native, clock: clock, interceptDb: interceptDb);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        return kit;
    }

    private sealed class FaultyEncoder : IKeyMoveHashCodec<Item>
    {
        private readonly ItemHashCodec _inner = new(false, true);

        public bool CachesNulls => _inner.CachesNulls;

        public bool CarriesMetadata => _inner.CarriesMetadata;

        public IReadOnlyList<KeyValuePair<string, byte[]>> EncodeFields(IDictionary<string, Item?> fields, IDictionary<string, string?>? metadata) =>
            throw new InvalidOperationException("The fields cannot be encoded.");

        public DecodedHash<Item> DecodeFields(IReadOnlyCollection<KeyValuePair<string, byte[]>> stored, bool capped = true) => _inner.DecodeFields(stored, capped);
    }
}
