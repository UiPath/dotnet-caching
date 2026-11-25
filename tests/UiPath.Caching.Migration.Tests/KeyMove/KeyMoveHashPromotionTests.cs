using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The forward promotion of a hash: the staging key, PUBH, CMPDELH, the size classes and the help a reader gives a live supersession marker, on the real scripts and on the transaction fallback.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashPromotionTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Original = new() { ["a"] = "1", ["b"] = "2", ["c"] = "3" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_promotion_stages_the_hash_in_the_slot_of_N_and_publishes_it_whole_with_the_source_life_and_no_version(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(5));
        var before = await kit.Kit.FenceAsync("k");

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Fields!.ToDictionary(f => f.Key, f => f.Value!.Name).Should().BeEquivalentTo(Original);
        (await kit.AllFieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original, "no system field is added and no version is minted by a promotion");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(4)).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        (await kit.Kit.FenceAsync("k")).Should().NotBe(before);
        (await StagingKeysAsync(kit)).Should().BeEmpty("the staging key is gone");
        kit.Kit.Schedule.Steps.Should().Equal(
            SchedulePointStep.ReadDestination,
            SchedulePointStep.ReadDestinationOnMaster,
            SchedulePointStep.ProbeSource,
            SchedulePointStep.Capture,
            SchedulePointStep.ReadSource,
            SchedulePointStep.StageHash,
            SchedulePointStep.Publish,
            SchedulePointStep.ConfirmDestination,
            SchedulePointStep.CompareDelete);
    }

    [Fact]
    public async Task Until_the_publish_the_fields_are_in_a_staging_key_with_the_life_of_the_fence_and_N_is_not_there()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        bool? nExists = null;
        long staged = 0;
        TimeSpan? life = null;
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Publish)
            {
                nExists = await kit.Db.KeyExistsAsync(kit.N("k"));
                var staging = (await StagingKeysAsync(kit)).Single();
                staged = await kit.Db.HashLengthAsync(staging);
                life = await kit.Db.KeyTimeToLiveAsync(staging);
            }
        };

        await kit.Client.ReadAsync("k", null, Ct);

        nExists.Should().BeFalse("N is created by one rename of the staging key and never grows field by field");
        staged.Should().Be(Original.Count);
        life.Should().NotBeNull("the staging key never lives longer than the fence");
        life.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_hash_with_no_expiry_is_published_with_none()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);

        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Found);

        (await kit.Db.KeyTimeToLiveAsync(kit.N("k"))).Should().BeNull("the staging key's life does not carry over");
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
    }

    [Fact]
    public async Task A_hash_of_more_fields_than_one_staging_chunk_is_published_whole()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var many = Enumerable.Range(0, 1300).ToDictionary(i => $"f{i}", i => $"v{i}");
        await kit.SetAsync(kit.L("k"), many);

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Fields!.Count.Should().Be(1300);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(many);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    // A field write, a field delete and a replace that run between the staging and the publish; N is never partial, and the fields are exact at the end.
    [Theory]
    [InlineData("merge", true)]
    [InlineData("merge", false)]
    [InlineData("delete-field", true)]
    [InlineData("delete-field", false)]
    [InlineData("replace", true)]
    [InlineData("replace", false)]
    public async Task A_promotion_that_races_a_field_write_a_field_delete_or_a_replace_ends_with_exact_fields_and_never_shows_a_partial_hash(string race, bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        var expected = race switch
        {
            "merge" => new Dictionary<string, string>(Original) { ["d"] = "4" },
            "delete-field" => new Dictionary<string, string> { ["b"] = "2", ["c"] = "3" },
            _ => new Dictionary<string, string> { ["x"] = "9" },
        };
        var seen = new List<Dictionary<string, string>>();
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (await kit.Db.KeyExistsAsync(kit.N("k")))
            {
                seen.Add(await kit.FieldsAsync(kit.N("k")));
            }

            if (step == SchedulePointStep.Publish && nth == 1)
            {
                WriteResult raced = race switch
                {
                    "merge" => await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), new HashWriteOptions(HashCacheSetOption.HashReplace, null), Ct),
                    "delete-field" => await kit.Client.RemoveFieldsAsync("k", ["a"], Ct),
                    _ => await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null), Ct),
                };
                raced.Outcome.Should().Be(WriteOutcome.Committed);
            }
        };

        var read = await kit.Client.ReadAsync("k", null, Ct);

        kit.Kit.Schedule.Hook = null;
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(expected);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        read.Fields!.ToDictionary(f => f.Key, f => f.Value!.Name).Should().BeEquivalentTo(expected, "the read ends on what N holds after the fence moved");
        foreach (var fields in seen)
        {
            IsWholeHash(race, fields).Should().BeTrue($"N was seen as {string.Join(',', fields.Keys)}");
        }
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public async Task A_hash_above_the_field_cap_is_dropped_by_SRCDEL_for_a_cache_with_an_origin_without_being_read_and_one_at_the_cap_is_promoted(int fields, bool dropped)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, maxFields: 3);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Enumerable.Range(0, fields).ToDictionary(i => $"f{i}", i => $"v{i}"));
        var original = Spy(kit, out var calls);

        var read = await kit.Client.ReadAsync("k", null, Ct);

        kit.Kit.Plane.Held = original;
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("promoted or dropped, L is gone");
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().Be(!dropped);
        read.Kind.Should().Be(dropped ? ReadOutcome.Absent : ReadOutcome.Found, "a dropped hash is a miss, and the next read reloads it from the origin");
        if (dropped)
        {
            calls.Should().Contain(c => c.Method == "HashLengthAsync" && c.Key == kit.L("k"));
            calls.Should().NotContain(c => c.Method.StartsWith("HashGetAll", StringComparison.Ordinal) && c.Key == kit.L("k"));
            calls.Count(c => c.Method == "CreateTransaction").Should().Be(1, "only the master read of N; L was never read");
        }
    }

    [Fact]
    public async Task A_hash_above_the_field_cap_of_a_cache_with_no_origin_stays_and_is_served_whole_from_L_uncached()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true, maxFields: 3);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var big = Enumerable.Range(0, 5).ToDictionary(i => $"f{i}", i => $"v{i}");
        await kit.SetAsync(kit.L("k"), big);

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Uncached.Should().BeTrue();
        read.Fields!.Count.Should().Be(5);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(big, "without an origin L is the only copy");
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_hash_above_the_byte_cap_is_dropped_for_a_cache_with_an_origin_and_kept_without_one(bool noOrigin, bool dropped)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: noOrigin, options: new KeyMoveOptions { MaxValueBytes = 20 });
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["a"] = new string('x', 30) });

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found, "what was read is served");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().Be(!dropped);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("a hash above the byte cap is never promoted");
    }

    // The same rule in S1 and S2: nothing is promoted, so nothing is dropped, and the hash is read from L whatever its size.
    [Theory]
    [InlineData("S1")]
    [InlineData("S2")]
    public async Task A_hash_above_the_field_cap_is_neither_dropped_nor_promoted_in_S1_and_S2(string state)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, maxFields: 3);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, nPop: 0);
        var big = Enumerable.Range(0, 5).ToDictionary(i => $"f{i}", i => $"v{i}");
        await kit.SetAsync(kit.L("k"), big);

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Fields!.Count.Should().Be(5);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(big);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task An_unreadable_hash_at_the_cap_stays_and_one_above_it_is_dropped_like_a_readable_one()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, maxFields: 3);
        await kit.Kit.PrepareAsync("k", "big");
        await kit.Kit.SetStateAsync("S3");
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["a"] = "1", ["b"] = "junk:x", ["c"] = "3" });
        await kit.SetAsync(kit.L("big"), new Dictionary<string, string> { ["a"] = "1", ["b"] = "junk:x", ["c"] = "3", ["d"] = "4" });

        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unreadable);
        (await kit.Client.ReadAsync("big", null, Ct)).Kind.Should().Be(ReadOutcome.Absent);

        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue("an unreadable hash is never dropped");
        (await kit.Kit.ExistsAsync(kit.L("big"))).Should().BeFalse("the inventory judges by the field count alone, so the read drops what it calls droppable");
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        meter.Count("move_unreadable_source").Should().Be(1);
    }

    [Fact]
    public async Task A_field_name_of_the_reserved_pattern_on_L_makes_the_hash_unmappable_and_it_is_left_where_it_is()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var fields = new Dictionary<string, string> { ["a"] = "1", ["_x_"] = "2" };
        await kit.SetAsync(kit.L("k"), fields);
        using var meter = new MeterCollector(kit.Kit.Cid);

        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unreadable);

        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(fields);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        meter.Count("move_unreadable_source").Should().Be(1);
    }

    // A cached empty hash in N is the answer; the read never falls back to L for it.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task A_cached_empty_hash_in_N_stops_the_read_only_where_N_caches_nulls(bool nativeNulls, bool stops)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, nativeNulls: nativeNulls);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.Db.HashSetAsync(kit.N("k"), nativeNulls ? [new HashEntry("_metadata_", string.Empty)] : [new HashEntry("_version_", "7")]);
        await kit.SetAsync(kit.L("k"), Original);

        var read = await kit.Client.ReadAsync("k", null, Ct);

        if (stops)
        {
            read.Kind.Should().Be(ReadOutcome.Absent);
            read.Stops.Should().BeTrue();
            (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue("L is not consulted, and not deleted");
            kit.Kit.Schedule.Steps.Should().NotContain(SchedulePointStep.Capture);
        }
        else
        {
            read.Kind.Should().Be(ReadOutcome.Absent, "an N with a count only is no hash, but it is the key N holds, which a promotion never publishes over");
            read.Stops.Should().BeFalse();
            (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        }
    }

    // By field: an empty field is a null; N keeps it as an empty value where it caches nulls and leaves it out where it does not.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_null_field_is_promoted_as_an_empty_value_where_N_caches_nulls_and_left_out_where_it_does_not(bool nativeNulls)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, nativeNulls: nativeNulls);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["a"] = "1", ["b"] = string.Empty });

        await kit.Client.ReadAsync("k", null, Ct);

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(nativeNulls ? new Dictionary<string, string> { ["a"] = "1", ["b"] = string.Empty } : new Dictionary<string, string> { ["a"] = "1" });
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("the compare of L used every field as read");
    }

    [Fact]
    public async Task A_hash_of_nothing_but_null_fields_is_dropped_where_N_does_not_cache_nulls_and_is_a_miss()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["a"] = string.Empty });

        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Absent);

        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_field_read_of_a_key_that_is_only_in_L_promotes_the_whole_hash_and_a_field_miss_on_an_existing_N_hash_is_a_miss()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);

        var one = await kit.Client.ReadAsync("k", ["b", "zz"], Ct);

        one.Fields!.Keys.Should().BeEquivalentTo("b");
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original, "the whole hash moved");

        // An L that came back for the key is not looked at: N is complete.
        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["zz"] = "9" });
        (await kit.Client.ReadAsync("k", ["zz"], Ct)).Fields.Should().BeEmpty();
    }

    [Fact]
    public async Task A_read_in_S5_and_in_S4_with_residue_is_the_union_with_no_promotion()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S4");
        await kit.SetAsync(kit.L("k"), Original);

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Fields!.Count.Should().Be(3);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue("S4 has no promoter");
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        kit.Kit.Schedule.Steps.Should().NotContain(SchedulePointStep.Capture);
    }

    // A promoter that meets a live marker. A no-origin write set N with its marker and N was then removed; the older copy of L must not be promoted over the newer write.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_promoter_that_meets_a_live_marker_deletes_the_older_hash_removes_the_marker_and_publishes_nothing(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, noOrigin: true);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.Db.HashSetAsync(kit.Kit.Control("k"), kit.Kit.Field("k"), $"{kit.Kit.Id}|n0");

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Absent);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("the older copy is deleted by the helper");
        (await kit.Kit.MarkerAsync("k")).Should().BeNull();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await StagingKeysAsync(kit)).Should().BeEmpty("no staging key is built for a marker");
        kit.Kit.Schedule.Steps.Should().NotContain(SchedulePointStep.StageHash).And.NotContain(SchedulePointStep.Publish);
        meter.Count("cache.keymove.superseded_helped").Should().Be(1);
    }

    // The marker's write expired in N before the promoter came; the marker still says L is older.
    [Fact]
    public async Task A_promoter_meets_the_marker_of_a_write_whose_hash_has_already_expired_in_N()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        kit.Kit.Time.UtcOffset = TimeSpan.FromSeconds(-1);
        var deadline = (await KeyMoveKit.ServerTimeAsync(kit.Db)).AddMilliseconds(300);
        var written = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("z", "1")), new HashWriteOptions(HashCacheSetOption.KeyReplace, deadline), Ct);
        written.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeTrue();
        await kit.SetAsync(kit.L("k"), Original);
        await kit.Db.HashSetAsync(kit.Kit.Control("k"), kit.Kit.Field("k"), $"{kit.Kit.Id}|n1");
        await Task.Delay(350, Ct);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("the write has expired");

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Absent);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("the older copy is never published over the newer write");
    }

    // The promotion's entry point for a merge or a mover pass: it tells whether L is gone, and CMPDELH that lost to a change leaves L and says so.
    [Fact]
    public async Task An_ensure_promotion_is_complete_when_L_is_gone_and_changing_when_a_write_changed_L_after_the_compare_began()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k", "m");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.SetAsync(kit.L("m"), Original);

        var done = await kit.Client.Promotion.PromoteAsync("k", HashPromotionFlavour.Ensure, Ct);
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.CompareDelete)
            {
                await kit.Db.HashSetAsync(kit.L("m"), "late", "1");
            }
        };
        var changing = await kit.Client.Promotion.PromoteAsync("m", HashPromotionFlavour.Ensure, Ct);

        done.Status.Should().Be(HashPromotionStatus.Complete);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        changing.Status.Should().Be(HashPromotionStatus.Changing);
        (await kit.FieldsAsync(kit.N("m"))).Should().BeEquivalentTo(Original, "it was published whole before the change");
        (await kit.FieldsAsync(kit.L("m"))).Should().ContainKey("late", "the compare saw a change and kept L");
    }

    // The hash of a library format that carries metadata keeps it through the promotion; a reserved name that is no system field still makes the hash unmappable.
    [Fact]
    public async Task A_hash_whose_L_codec_carries_metadata_is_promoted_with_its_metadata_and_a_foreign_reserved_field_still_blocks_it()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, inPlace: new ItemHashCodec(false, true), native: new ItemHashCodec(false, true));
        await kit.Kit.PrepareAsync("k", "odd");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["a"] = "1", ["_metadata_"] = "{\"owner\":\"me\"}" }, TimeSpan.FromMinutes(5));
        await kit.SetAsync(kit.L("odd"), new Dictionary<string, string> { ["a"] = "1", ["_x_"] = "2" });

        var read = await kit.Client.ReadAsync("k", null, Ct);
        var odd = await kit.Client.ReadAsync("odd", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Metadata.Should().ContainKey("owner");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.AllFieldsAsync(kit.N("k"))).Should().ContainKey("_metadata_");
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "1" });
        odd.Kind.Should().Be(ReadOutcome.Unreadable);
        (await kit.Kit.ExistsAsync(kit.N("odd"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_read_in_S5_is_N_alone_and_ignores_the_copy_a_sweep_has_not_reached()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k", "m");
        await kit.Kit.SetStateAsync("S5");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.SetAsync(kit.N("m"), Original);

        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Absent);
        (await kit.Client.ReadAsync("m", null, Ct)).Fields!.Count.Should().Be(3);

        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_hash_whose_key_does_not_map_both_ways_is_served_from_L_and_never_promoted()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, mappable: _ => false);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Fields!.Count.Should().Be(3);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_read_under_rules_older_than_the_gate_runs_again_until_the_rules_follow_and_then_promotes()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, options: new KeyMoveOptions { MaxReruns = 2 });
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.Kit.MoveGateAsync();

        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unavailable);
        meter.Count("move_identity_rerun").Should().Be(3);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue("nothing was promoted under rules that are not the gate's");

        kit.Kit.ApplyRules("S3");
        (await kit.Client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Found);
    }

    [Fact]
    public async Task A_promotion_outside_S3_and_S4_asks_the_caller_to_plan_again()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", nPop: 0);

        (await kit.Client.Promotion.PromoteAsync("k", HashPromotionFlavour.Read, Ct)).Status.Should().Be(HashPromotionStatus.Replan);
    }

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

    // The hash client reads through a view that records every call it makes with the key it names.
    private static KeyMoveData Spy(KeyMoveHashKit kit, out List<(string Method, string Key)> calls)
    {
        var recorded = new List<(string, string)>();
        calls = recorded;
        var held = kit.Kit.Plane.Held!;
        var proxy = InterceptingProxy.Wrap(held.Db, (method, args) =>
        {
            lock (recorded)
            {
                recorded.Add((method.Name, args.OfType<RedisKey>().FirstOrDefault().ToString()));
            }

            return InterceptingProxy.Proceed;
        });
        kit.Kit.Plane.Held = held with { Db = proxy };
        return held;
    }

    private static bool IsWholeHash(string race, Dictionary<string, string> fields) => race switch
    {
        "merge" => fields.Count is 3 or 4 && fields["a"] == "1",
        "delete-field" => fields.Count is 2 or 3,
        _ => fields.Count is 3 or 1,
    };
}
