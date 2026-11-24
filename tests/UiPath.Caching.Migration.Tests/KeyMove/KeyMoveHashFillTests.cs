using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Hash fills: a generator result on a miss is a whole-hash NX create. In S1 and S2 it is FILLH on L with no fence; in S3 and S4 it is FILLH on N's staging key with the fence replaced; in S5 it is native.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashFillTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Filled = new() { ["a"] = "1", ["b"] = "2" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("S1", true)]
    [InlineData("S1", false)]
    [InlineData("S2", true)]
    public async Task In_S1_and_S2_a_fill_creates_L_from_a_staging_key_with_no_fence_and_a_relative_life(string state, bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, nPop: 0);

        var filled = await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1"), ("b", "2")), Fill(DateTimeOffset.UtcNow.AddMinutes(5)), null, Ct);

        filled.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Filled, "the in-place side keeps no system field");
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
        (await kit.Kit.FenceAsync("k")).Should().BeNull("L has no fence to replace");
        (await StagingAsync(kit)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_fill_that_loses_to_a_hash_of_L_writes_nothing_and_leaves_the_winner_in_the_box_and_no_staging_key()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", nPop: 0);
        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["w"] = "won" }, TimeSpan.FromMinutes(3));
        var box = new HashFillBox<Support.Item>();

        var filled = await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Fill(), box, Ct);

        filled.Outcome.Should().Be(WriteOutcome.NotSent);
        box.Lost.Should().BeTrue();
        box.Stored!.Keys.Should().BeEquivalentTo("w");
        box.StoredExpiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(3), TimeSpan.FromSeconds(30));
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["w"] = "won" });
        (await StagingAsync(kit)).Should().BeEmpty("a lost fill deletes its staging key");
    }

    [Theory]
    [InlineData("S3", true)]
    [InlineData("S3", false)]
    [InlineData("S4", true)]
    public async Task In_S3_and_S4_a_fill_creates_N_from_its_staging_key_with_a_version_less_hash_and_replaces_the_fence(string state, bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state);
        var before = await kit.Kit.FenceAsync("k");

        var filled = await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1"), ("b", "2")), Fill(), null, Ct);

        filled.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Filled);
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k"))).Should().BeNull("no deadline persists, whatever the staging key's life was");
        (await kit.Kit.FenceAsync("k")).Should().NotBe(before).And.NotBeNull();
        (await StagingAsync(kit)).Should().BeEmpty();
    }

    // The fill replaces the fence, so a promoter that captured before it is refused and cannot overwrite the fill.
    [Fact]
    public async Task A_promoter_that_captured_before_a_fill_is_refused_at_its_publish()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var keys = new FencedKeys(kit.Kit.Names.ForwardFence(kit.N("k")), kit.N("k"), kit.Kit.Names.ControlFor(kit.Kit.Group, kit.N("k")));
        var capture = await kit.Kit.Scripts.CaptureAsync(keys, kit.Kit.Id, kit.Kit.Cid, MoveScripts.NewNonce(), 60_000);
        capture.Captured.Should().BeTrue();

        (await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Fill(), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        var staging = kit.Kit.Names.ForwardStaging(kit.N("k"), "late");
        await kit.Db.HashSetAsync(staging, "old", "copy");
        var published = await kit.Kit.Scripts.PublishHashAsync(keys, staging, kit.Kit.Id, new Publication(capture.Token!, capture.Gate!, kit.Kit.Cid, MoveScripts.NewNonce(), 60_000, MoveScripts.NoExpiry, MoveScripts.NoExpiry, true));

        published.Should().Be(MoveScripts.FenceMoved);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "1" });
    }

    [Fact]
    public async Task A_fill_that_loses_to_a_hash_of_N_returns_the_winner_and_a_cached_empty_winner_is_the_answer()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, nativeNulls: true);
        await kit.Kit.PrepareAsync("k", "e");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.N("k"), new Dictionary<string, string> { ["w"] = "won" });
        await kit.Db.HashSetAsync(kit.N("e"), "_metadata_", string.Empty);
        var box = new HashFillBox<Support.Item>();
        var empty = new HashFillBox<Support.Item>();

        (await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Fill(), box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await kit.Client.FillAsync("e", KeyMoveHashKit.Items(("a", "1")), Fill(), empty, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        box.Stored!.Keys.Should().BeEquivalentTo("w");
        empty.Lost.Should().BeTrue();
        empty.CachedEmpty.Should().BeTrue();
        empty.Stored.Should().BeNull();
        (await StagingAsync(kit)).Should().BeEmpty();
    }

    [Fact]
    public async Task In_S5_a_fill_is_a_native_create_that_never_replaces_a_hash()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S5", swept: true);

        (await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Fill(), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("z", "2")), Fill(), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "1" });
        (await kit.Kit.FenceAsync("k")).Should().BeNull();
    }

    [Fact]
    public async Task A_fill_with_nothing_to_store_or_no_life_left_is_not_sent_and_an_empty_hash_is_a_cached_empty_hash_where_nulls_are_cached()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");

        (await kit.Client.FillAsync("k", KeyMoveHashKit.Items(), Fill(), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Fill(DateTimeOffset.UtcNow.AddSeconds(-1)), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();

        await using var nulls = await KeyMoveHashKit.CreateAsync(fixture, nativeNulls: true);
        await nulls.Kit.PrepareAsync("k");
        await nulls.Kit.SetStateAsync("S3");
        (await nulls.Client.FillAsync("k", KeyMoveHashKit.Items(), Fill(), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await nulls.AllFieldsAsync(nulls.N("k"))).Should().ContainKey("_metadata_");
    }

    // The deadline of a fill is an absolute time that holds to the publication: time spent staging or waiting for a new identity is not given back.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_fill_delayed_past_its_deadline_between_staging_and_publication_publishes_nothing(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var before = await kit.Kit.FenceAsync("k");
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Fill)
            {
                await Task.Delay(1200, Ct);
            }
        };

        var filled = await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Fill(DateTimeOffset.UtcNow.AddMilliseconds(700)), null, Ct);

        filled.Outcome.Should().Be(WriteOutcome.NotSent);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.Kit.FenceAsync("k")).Should().Be(before, "a fill that publishes nothing replaces nothing");
        (await StagingAsync(kit)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_fill_that_reruns_for_a_new_identity_keeps_the_original_deadline_and_does_not_get_its_life_again(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Fill && nth == 1)
            {
                await Task.Delay(2500, Ct);
                await kit.Kit.SetStateAsync("S3");
            }
        };

        var filled = await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Fill(deadline), null, Ct);

        filled.Outcome.Should().Be(WriteOutcome.Committed);
        kit.Kit.Schedule.Steps.Count(s => s == SchedulePointStep.Fill).Should().Be(2, "the identity changed under the first attempt");
        var left = await kit.Db.KeyTimeToLiveAsync(kit.N("k"));
        left!.Value.Should().BeLessThan(TimeSpan.FromMilliseconds(2000), "the life is what is left of the first deadline, not four seconds again");
        left.Value.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_fill_with_no_deadline_has_no_expiry_and_one_whose_deadline_has_passed_stages_and_sends_nothing(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k", "late");
        await kit.Kit.SetStateAsync("S3");

        (await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Fill(), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        kit.Kit.Schedule.Reset();
        var expired = await kit.Client.FillAsync("late", KeyMoveHashKit.Items(("a", "1")), Fill(DateTimeOffset.UtcNow.AddMilliseconds(-5)), null, Ct);

        (await kit.Db.KeyTimeToLiveAsync(kit.N("k"))).Should().BeNull();
        expired.Outcome.Should().Be(WriteOutcome.NotSent);
        kit.Kit.Schedule.Steps.Should().NotContain(SchedulePointStep.StageHash);
        (await kit.Kit.ExistsAsync(kit.N("late"))).Should().BeFalse();
    }

    // The script itself refuses a deadline that passed on the server's clock, and the transaction fallback sets the expiry as an absolute time.
    [Fact]
    public async Task The_fill_script_refuses_a_deadline_that_has_passed_and_deletes_the_staging_key_and_one_in_time_keeps_the_absolute_expiry()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k", "late");
        await kit.Kit.SetStateAsync("S3");
        var names = kit.Kit.Names;
        var inTime = names.ForwardStaging(kit.N("k"), "t1");
        var tooLate = names.ForwardStaging(kit.N("late"), "t2");
        await kit.Db.HashSetAsync(inTime, "a", "1");
        await kit.Db.HashSetAsync(tooLate, "a", "1");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();

        var published = await kit.Kit.Scripts.FillHashUntilAsync(kit.N("k"), names.ControlFor(kit.Kit.Group, kit.N("k")), inTime, kit.Kit.Id, deadline, null);
        var late = await kit.Kit.Scripts.FillHashUntilAsync(kit.N("late"), names.ControlFor(kit.Kit.Group, kit.N("late")), tooLate, kit.Kit.Id, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds(), null);

        published.Should().Be(MoveScripts.Published);
        (await kit.Db.KeyExpireTimeAsync(kit.N("k")))!.Value.Should().BeCloseTo(DateTimeOffset.FromUnixTimeMilliseconds(deadline).UtcDateTime, TimeSpan.FromSeconds(1));
        late.Should().Be(MoveScripts.Late);
        (await kit.Kit.ExistsAsync(kit.N("late"))).Should().BeFalse();
        (await kit.Db.KeyExistsAsync(tooLate)).Should().BeFalse("a refused fill leaves no staging key");
    }

    // The library hash of a key that has moved can hold metadata; the older in-place format cannot, so a write of it is refused where it would be stored there.
    [Fact]
    public async Task A_fill_that_carries_metadata_is_refused_in_S1_where_L_cannot_hold_it_and_stored_where_L_can()
    {
        var metadata = new Dictionary<string, string?> { ["owner"] = "me" };
        await using var plain = await KeyMoveHashKit.CreateAsync(fixture);
        await plain.Kit.PrepareAsync("k");
        await plain.Kit.SetStateAsync("S1", nPop: 0);
        await using var library = await KeyMoveHashKit.CreateAsync(fixture, inPlace: new ItemHashCodec(false, true));
        await library.Kit.PrepareAsync("k");
        await library.Kit.SetStateAsync("S1", nPop: 0);

        var refused = async () => await plain.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null, metadata), null, Ct);
        var stored = await library.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null, metadata), null, Ct);

        (await refused.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain(plain.Kit.Cid);
        (await plain.Kit.ExistsAsync(plain.L("k"))).Should().BeFalse("nothing is stored by a refused write");
        stored.Outcome.Should().Be(WriteOutcome.Committed);
        (await library.AllFieldsAsync(library.L("k"))).Keys.Should().Contain("_metadata_");
    }

    private static async Task<List<StackExchange.Redis.RedisKey>> StagingAsync(KeyMoveHashKit kit)
    {
        await Task.CompletedTask;
        return [.. kit.Kit.Mux.GetServers().Where(s => !s.IsReplica).SelectMany(s => s.Keys(pattern: $"*{kit.Kit.Unique}*:stg:*").Concat(s.Keys(pattern: $"*{kit.Kit.Unique}*:s:*")))];
    }

    private static HashWriteOptions Fill(DateTimeOffset? deadline = null) => new(HashCacheSetOption.KeyReplace, deadline);
}
