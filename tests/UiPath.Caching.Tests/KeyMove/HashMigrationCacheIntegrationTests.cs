using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Redis;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The plain library path of a hash against a real server, bare and layered: what a snapshot finds, and that a write, delete and refresh store what the library's own do.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class HashMigrationCacheIntegrationTests(RedisContainerFixture fixture)
{
    private static readonly string RunTag = Guid.NewGuid().ToString("N")[..8];
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);
    private static readonly HashCacheEntryOptions Replace = new(TimeToLive: Life);

    private ManualTimeProvider? _time;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ManualTimeProvider Time => _time!;

    public static TheoryData<bool, bool, bool, bool, bool> RenewCases()
    {
        var data = new TheoryData<bool, bool, bool, bool, bool>();
        foreach (var exists in new[] { true, false })
        {
            foreach (var cacheNulls in new[] { false, true })
            {
                foreach (var withMetadata in new[] { false, true })
                {
                    foreach (var storedWithTtl in new[] { true, false })
                    {
                        foreach (var persist in new[] { false, true })
                        {
                            data.Add(exists, cacheNulls, withMetadata, storedWithTtl, persist);
                        }
                    }
                }
            }
        }

        return data;
    }

    public static TheoryData<string, string, bool> KindCases()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var kind in new[] { "hash", "string", "missing" })
        {
            foreach (var expiry in new[] { "past", "future", "persistent" })
            {
                foreach (var withMetadata in new[] { false, true })
                {
                    data.Add(kind, expiry, withMetadata);
                }
            }
        }

        return data;
    }

    public static TheoryData<string, bool, bool, bool> MergeCases()
    {
        var data = new TheoryData<string, bool, bool, bool>();
        foreach (var kind in new[] { "absent", "hash", "string" })
        {
            foreach (var seedPersistent in new[] { false, true })
            {
                foreach (var writePersistent in new[] { false, true })
                {
                    foreach (var chunked in new[] { false, true })
                    {
                        data.Add(kind, seedPersistent, writePersistent, chunked);
                    }
                }
            }
        }

        return data;
    }

    [Fact]
    public async Task A_snapshot_finds_the_fields_the_metadata_and_the_lifetime_together()
    {
        using var harness = Harness(layered: false);
        var cache = harness.Hashes();
        var key = Key();
        await cache.SetAsync(key, new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y" }, Replace with { Metadata = new Dictionary<string, string?> { ["owner"] = "me" } }, null, Ct);

        var snapshot = await Migration(cache).ReadSnapshotAsync<string>(key, null, Ct);

        snapshot.Outcome.Should().Be(SnapshotOutcome.Found);
        snapshot.Entry!.Value.Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y" });
        snapshot.Entry.Metadata.Should().BeEquivalentTo(new Dictionary<string, string?> { ["owner"] = "me" });
        snapshot.Entry.Expiration.Should().BeCloseTo(Time.ToDateTimeOffset(Life), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_snapshot_of_a_missing_hash_is_absent_of_an_unreadable_one_unreadable_and_it_is_left_in_place_and_of_a_string_failed()
    {
        using var harness = Harness(layered: false);
        var cache = harness.Hashes();
        var migration = Migration(cache);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();

        (await migration.ReadSnapshotAsync<string>(Key(), null, Ct)).Outcome.Should().Be(SnapshotOutcome.Absent);

        var bad = Key();
        await cache.SetAsync(bad, new Dictionary<string, string?> { ["a"] = "x" }, Replace, null, Ct);
        var badKey = Find(raw, bad);
        await db.HashSetAsync(badKey, "junk", new byte[] { 0x1E, 1, 2, 3 });
        (await migration.ReadSnapshotAsync<string>(bad, null, Ct)).Outcome.Should().Be(SnapshotOutcome.Unreadable);
        (await db.HashGetAllAsync(badKey)).Should().HaveCount(2, "an unreadable hash is left in place");

        var text = Key();
        await cache.SetAsync(text, new Dictionary<string, string?> { ["a"] = "x" }, Replace, null, Ct);
        var textKey = Find(raw, text);
        await db.KeyDeleteAsync(textKey);
        await db.StringSetAsync(textKey, "not a hash", Life);
        (await migration.ReadSnapshotAsync<string>(text, null, Ct)).Outcome.Should().Be(SnapshotOutcome.Failed);
    }

    [Fact]
    public async Task A_write_stores_what_the_libraries_own_write_stores_a_merge_keeps_the_other_fields_and_the_lifetime_is_the_keys()
    {
        using var harness = Harness(layered: false);
        var cache = harness.Hashes();
        var migration = Migration(cache);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var fields = new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y" };
        var options = Replace with { Metadata = new Dictionary<string, string?> { ["owner"] = "me" } };
        var viaSet = Key();
        var viaWrite = Key();

        await cache.SetAsync(viaSet, fields, options, null, Ct);
        (await migration.WriteAsync(viaWrite, fields, options, null, Ct)).Should().Be(StoreOutcome.Committed);

        (await StoredAsync(raw, viaWrite)).Should().BeEquivalentTo(await StoredAsync(raw, viaSet));
        (await raw.GetDatabase().KeyTimeToLiveAsync(Find(raw, viaWrite)))!.Value.Should().BeCloseTo(Life, TimeSpan.FromSeconds(15));

        (await migration.WriteAsync(viaWrite, new Dictionary<string, string?> { ["c"] = "z" }, options with { SetOption = HashCacheSetOption.HashReplace }, null, Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.GetAsync<string>(viaWrite, Ct)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y", ["c"] = "z" });
        (await migration.WriteAsync(viaWrite, new Dictionary<string, string?> { ["d"] = "w" }, options, null, Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.GetAsync<string>(viaWrite, Ct)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["d"] = "w" });
    }

    [Fact]
    public async Task A_delete_and_a_field_delete_remove_what_they_name_and_a_refresh_moves_only_the_lifetime_of_a_hash_that_is_there()
    {
        using var harness = Harness(layered: false);
        var cache = harness.Hashes();
        var migration = Migration(cache);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Key();
        await cache.SetAsync(key, new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y" }, Replace, null, Ct);

        (await migration.DeleteFieldsAsync<string>(key, ["a"], Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.GetAsync<string>(key, Ct)).Should().BeEquivalentTo(new Dictionary<string, string?> { ["b"] = "y" });

        (await migration.RenewAsync<string>(key, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromHours(2)), null, Ct)).Should().Be(StoreOutcome.Committed);
        (await raw.GetDatabase().KeyTimeToLiveAsync(Find(raw, key)))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(1));

        (await migration.DeleteAsync<string>(key, Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.GetAsync<string>(key, Ct)).Should().BeEmpty();
        (await migration.RenewAsync<string>(key, Replace, null, Ct)).Should().Be(StoreOutcome.Failed, "there is no hash to refresh");
    }

    // The library's refresh with options answers true for a hash that is not there while nulls are not cached, and makes a key of the metadata alone; the renew leaves it absent.
    [Theory]
    [MemberData(nameof(RenewCases))]
    public async Task A_renew_has_the_effect_of_the_libraries_refresh_on_a_hash_that_is_there_and_leaves_one_that_is_not_absent(bool exists, bool cacheNulls, bool withMetadata, bool storedWithTtl, bool persist)
    {
        using var harness = Harness(layered: false, cacheNulls);
        var cache = harness.Hashes();
        var migration = Migration(cache);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        var (byLibrary, byRenew) = (Key(), Key());
        var seed = new HashCacheEntryOptions(TimeToLive: storedWithTtl ? Life : null, ExpireTime: storedWithTtl ? null : DateTimeOffset.MaxValue, Metadata: new Dictionary<string, string?> { ["old"] = "1" });
        var options = new HashCacheEntryOptions(TimeToLive: persist ? null : TimeSpan.FromHours(2), ExpireTime: persist ? DateTimeOffset.MaxValue : null, Metadata: withMetadata ? new Dictionary<string, string?> { ["m"] = "2" } : null);
        RedisKey? libraryKey = null;
        RedisKey? renewKey = null;
        if (exists)
        {
            await cache.SetAsync(byLibrary, new Dictionary<string, string?> { ["a"] = "x" }, seed, null, Ct);
            await cache.SetAsync(byRenew, new Dictionary<string, string?> { ["a"] = "x" }, seed, null, Ct);
            (libraryKey, renewKey) = (Find(raw, byLibrary), Find(raw, byRenew));
        }

        var library = await cache.RefreshAsync<string>(byLibrary, options, null, Ct);
        var outcome = await migration.RenewAsync<string>(byRenew, options, null, Ct);

        if (exists)
        {
            library.Should().BeTrue();
            outcome.Should().Be(StoreOutcome.Committed, "the hash was there, with or without a lifetime to remove");
            (await FieldsAsync(db, renewKey!.Value)).Should().BeEquivalentTo(await FieldsAsync(db, libraryKey!.Value), "the metadata is replaced or cleared as the library does");
            var (ttlOfLibrary, ttlOfRenew) = (await db.KeyTimeToLiveAsync(libraryKey.Value), await db.KeyTimeToLiveAsync(renewKey.Value));
            (ttlOfRenew is null).Should().Be(persist, "a persistent request leaves no lifetime");
            if (!persist)
            {
                ttlOfRenew!.Value.Should().BeCloseTo(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1), "the requested lifetime, not another one");
                ttlOfRenew.Value.Should().BeCloseTo(ttlOfLibrary!.Value, TimeSpan.FromSeconds(30), "the library sets the same");
            }
        }
        else
        {
            library.Should().Be(!cacheNulls, "the library's own answer, which a missing key does not change");
            outcome.Should().Be(StoreOutcome.Failed);
            (await migration.ReadSnapshotAsync<string>(byRenew, null, Ct)).Outcome.Should().Be(SnapshotOutcome.Absent);
            raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{byRenew}*").Count().Should().Be(0, "nothing, not even the metadata, was written");
        }
    }

    [Theory]
    [MemberData(nameof(KindCases))]
    public async Task A_renew_acts_only_on_a_hash_whatever_the_expiry_and_leaves_a_string_or_a_missing_key_as_it_is(string kind, string expiry, bool withMetadata)
    {
        using var harness = Harness(layered: false);
        var cache = harness.Hashes();
        var migration = Migration(cache);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        var key = Key();
        RedisKey? redisKey = null;
        if (kind != "missing")
        {
            await cache.SetAsync(key, new Dictionary<string, string?> { ["a"] = "x" }, Replace, null, Ct);
            redisKey = Find(raw, key);
        }

        if (kind == "string")
        {
            await db.KeyDeleteAsync(redisKey!.Value);
            await db.StringSetAsync(redisKey.Value, "not a hash", Life);
        }

        var options = expiry switch
        {
            "past" => new HashCacheEntryOptions(ExpireTime: Time.ToDateTimeOffset(TimeSpan.FromMinutes(-1))),
            "future" => new HashCacheEntryOptions(TimeToLive: TimeSpan.FromHours(2)),
            _ => new HashCacheEntryOptions(ExpireTime: DateTimeOffset.MaxValue),
        };
        options = options with { Metadata = withMetadata ? new Dictionary<string, string?> { ["m"] = "v" } : null };

        var outcome = await migration.RenewAsync<string>(key, options, null, Ct);

        if (kind == "hash")
        {
            outcome.Should().Be(StoreOutcome.Committed);
            var ttl = await db.KeyTimeToLiveAsync(redisKey!.Value);
            switch (expiry)
            {
                case "past":
                    (await db.KeyExistsAsync(redisKey.Value)).Should().BeFalse();
                    break;
                case "future":
                    ttl!.Value.Should().BeCloseTo(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1));
                    break;
                default:
                    ttl.Should().BeNull();
                    break;
            }
        }
        else if (kind == "string")
        {
            outcome.Should().Be(StoreOutcome.Failed);
            ((string?)await db.StringGetAsync(redisKey!.Value)).Should().Be("not a hash", "a string is not deleted, and its lifetime is not touched");
            (await db.KeyTimeToLiveAsync(redisKey.Value))!.Value.Should().BeCloseTo(Life, TimeSpan.FromSeconds(30));
        }
        else
        {
            outcome.Should().Be(StoreOutcome.Failed);
            raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task A_renew_to_an_expiry_in_the_past_deletes_a_hash_that_is_there_and_is_failed_for_one_that_is_not()
    {
        using var harness = Harness(layered: false);
        var cache = harness.Hashes();
        var migration = Migration(cache);
        var key = Key();
        await cache.SetAsync(key, new Dictionary<string, string?> { ["a"] = "x" }, Replace, null, Ct);
        var past = new HashCacheEntryOptions(ExpireTime: Time.ToDateTimeOffset(TimeSpan.FromMinutes(-1)));

        (await migration.RenewAsync<string>(key, past, null, Ct)).Should().Be(StoreOutcome.Committed);
        (await migration.ReadSnapshotAsync<string>(key, null, Ct)).Outcome.Should().Be(SnapshotOutcome.Absent);
        (await migration.RenewAsync<string>(key, past, null, Ct)).Should().Be(StoreOutcome.Failed);
    }

    // The expiry-only refresh is awaited and answered, whatever the library does with its own: it has the library's effect on a hash that is there, keeps the metadata, and leaves a key that holds no hash as it is.
    [Theory]
    [InlineData("hash", "future", false, false)]
    [InlineData("hash", "future", true, false)]
    [InlineData("hash", "persistent", false, false)]
    [InlineData("hash", "persistent", true, false)]
    [InlineData("hash", "past", false, false)]
    [InlineData("hash", "future", false, true)]
    [InlineData("hash", "persistent", true, true)]
    [InlineData("string", "future", false, false)]
    [InlineData("string", "persistent", false, false)]
    [InlineData("missing", "future", false, false)]
    [InlineData("missing", "past", false, false)]
    public async Task An_expiry_only_renew_has_the_effect_of_the_libraries_expiry_overload_keeps_the_metadata_and_is_failed_for_a_key_with_no_hash(string kind, string expiry, bool cacheNulls, bool layered)
    {
        using var harness = Harness(layered, cacheNulls);
        var cache = harness.Hashes(layered);
        var migration = Migration(cache);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        var (byLibrary, byRenew) = (Key(), Key());
        var seed = new HashCacheEntryOptions(TimeToLive: Life, Metadata: new Dictionary<string, string?> { ["old"] = "1" });
        RedisKey? libraryKey = null;
        RedisKey? renewKey = null;
        if (kind != "missing")
        {
            await cache.SetAsync(byLibrary, new Dictionary<string, string?> { ["a"] = "x" }, seed, null, Ct);
            await cache.SetAsync(byRenew, new Dictionary<string, string?> { ["a"] = "x" }, seed, null, Ct);
            (libraryKey, renewKey) = (Find(raw, byLibrary), Find(raw, byRenew));
            if (kind == "string")
            {
                await db.KeyDeleteAsync(renewKey.Value);
                await db.StringSetAsync(renewKey.Value, "not a hash", Life);
            }
        }

        var deadline = expiry switch
        {
            "past" => Time.ToDateTimeOffset(TimeSpan.FromMinutes(-1)),
            "persistent" => DateTimeOffset.MaxValue,
            _ => Time.ToDateTimeOffset(TimeSpan.FromHours(2)),
        };
        var outcome = await migration.RenewExpiryAsync<string>(byRenew, new HashCacheEntryOptions(ExpireTime: deadline, Metadata: new Dictionary<string, string?> { ["ignored"] = "9" }), null, Ct);

        if (kind == "hash")
        {
            outcome.Should().Be(StoreOutcome.Committed);
            if (expiry == "past")
            {
                (await db.KeyExistsAsync(renewKey!.Value)).Should().BeFalse();
                return;
            }

            (await FieldsAsync(db, renewKey!.Value)).Should().ContainKey("_metadata_", "the metadata stays");
            (await cache.GetCacheEntryAsync<string>(byRenew, Ct)).Metadata.Should().BeEquivalentTo(new Dictionary<string, string?> { ["old"] = "1" });
            var ttl = await db.KeyTimeToLiveAsync(renewKey.Value);
            if (expiry == "persistent")
            {
                ttl.Should().BeNull();
            }
            else
            {
                ttl!.Value.Should().BeCloseTo(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1));
            }

            if (!layered)
            {
                await AssertLibraryOverloadAgreesAsync(cache, harness.Services.GetRequiredService<IRedisConnector>().Database, byLibrary, (libraryKey!.Value, renewKey.Value), deadline, expiry);
            }
        }
        else if (kind == "string")
        {
            outcome.Should().Be(StoreOutcome.Failed);
            ((string?)await db.StringGetAsync(renewKey!.Value)).Should().Be("not a hash");
            (await db.KeyTimeToLiveAsync(renewKey.Value))!.Value.Should().BeCloseTo(Life, TimeSpan.FromSeconds(30));
        }
        else
        {
            outcome.Should().Be(StoreOutcome.Failed);
            raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{byRenew}*").Should().BeEmpty();
        }
    }

    // A merge acts on an absent key or on a hash: a string is refused with nothing applied, its lifetime included, whether the write is persistent or finite, in one transaction or in chunks. A renew is refused by its condition.
    [Theory]
    [MemberData(nameof(MergeCases))]
    public async Task A_merge_acts_on_an_absent_key_or_a_hash_and_a_string_is_refused_with_its_contents_and_its_lifetime_untouched(string kind, bool seedPersistent, bool writePersistent, bool chunked)
    {
        using var harness = Harness(layered: false);
        var cache = harness.Hashes();
        var migration = Migration(cache);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        var key = Key();
        var seed = seedPersistent ? new HashCacheEntryOptions(ExpireTime: DateTimeOffset.MaxValue) : Replace;
        RedisKey? redisKey = null;
        if (kind != "absent")
        {
            await cache.SetAsync(key, new Dictionary<string, string?> { ["a"] = "x" }, seed, null, Ct);
            redisKey = Find(raw, key);
        }

        if (kind == "string")
        {
            await db.KeyDeleteAsync(redisKey!.Value);
            if (seedPersistent)
            {
                await db.StringSetAsync(redisKey.Value, "not a hash");
            }
            else
            {
                await db.StringSetAsync(redisKey.Value, "not a hash", Life);
            }
        }

        var fields = Enumerable.Range(0, chunked ? 1100 : 1).ToDictionary(i => $"f{i}", i => (string?)"y");
        var options = new HashCacheEntryOptions(TimeToLive: writePersistent ? null : TimeSpan.FromHours(2), ExpireTime: writePersistent ? DateTimeOffset.MaxValue : null, SetOption: HashCacheSetOption.HashReplace);

        var outcome = await migration.WriteAsync(key, fields, options, null, Ct);

        if (kind == "string")
        {
            outcome.Should().Be(StoreOutcome.Failed, "nothing was applied");
            ((string?)await db.StringGetAsync(redisKey!.Value)).Should().Be("not a hash");
            var ttl = await db.KeyTimeToLiveAsync(redisKey.Value);
            if (seedPersistent)
            {
                ttl.Should().BeNull("a persistent string does not take the expiry of the write");
            }
            else
            {
                ttl!.Value.Should().BeCloseTo(Life, TimeSpan.FromSeconds(30), "an expiring string does not become persistent or take the write's life");
            }

            (await migration.RenewAsync<string>(key, Replace, null, Ct)).Should().Be(StoreOutcome.Failed);
            (await db.KeyTimeToLiveAsync(redisKey.Value) is null).Should().Be(seedPersistent);
            return;
        }

        outcome.Should().Be(StoreOutcome.Committed);
        var written = Find(raw, key);
        (await db.HashLengthAsync(written)).Should().BeGreaterThanOrEqualTo(fields.Count);
        var left = await db.KeyTimeToLiveAsync(written);
        if (writePersistent)
        {
            left.Should().BeNull();
        }
        else
        {
            left!.Value.Should().BeCloseTo(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1));
        }
    }

    // Each chunk of a merge checks the type in the step that writes it, by script or by a watched transaction. A string from the start is refused by the first chunk with nothing applied; one that appears between two chunks is refused by the second, which leaves it as it is, and the earlier chunk has landed, so the write is unknown.
    [Theory]
    [InlineData(true, "start")]
    [InlineData(false, "start")]
    [InlineData(true, "between")]
    [InlineData(false, "between")]
    public async Task A_string_met_by_a_chunked_merge_is_refused_with_its_contents_and_lifetime_untouched_and_the_outcome_says_whether_a_chunk_landed(bool scripts, string when)
    {
        using var harness = Harness(layered: false);
        var cache = (global::UiPath.Caching.Redis.RedisHashCache)harness.Hashes();
        cache.ChunkScriptsEnabled = scripts;
        var migration = Migration(cache);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        var key = Key();
        await cache.SetAsync(key, new Dictionary<string, string?> { ["a"] = "x" }, Replace, null, Ct);
        var redisKey = Find(raw, key);
        var chunks = new List<int>();
        async Task MakeStringAsync()
        {
            await db.KeyDeleteAsync(redisKey);
            await db.StringSetAsync(redisKey, "not a hash", Life);
        }

        if (when == "start")
        {
            await MakeStringAsync();
        }

        cache.OnChunk = async index =>
        {
            chunks.Add(index);
            if (when == "between" && index == 1)
            {
                await MakeStringAsync();
            }
        };
        var fields = Enumerable.Range(0, 1100).ToDictionary(i => $"f{i}", i => (string?)"y");

        var outcome = await migration.WriteAsync(key, fields, Replace with { SetOption = HashCacheSetOption.HashReplace, TimeToLive = TimeSpan.FromHours(2) }, null, Ct);

        outcome.Should().Be(when == "start" ? StoreOutcome.Failed : StoreOutcome.Unknown, when == "start" ? "the first chunk was refused and nothing was applied" : "the first chunk landed before the second was refused");
        chunks.Should().Equal(when == "start" ? [0] : [0, 1], "no chunk is written after one was refused");
        ((string?)await db.StringGetAsync(redisKey)).Should().Be("not a hash");
        (await db.KeyTimeToLiveAsync(redisKey))!.Value.Should().BeCloseTo(Life, TimeSpan.FromSeconds(30), "the string does not take the expiry of the write");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_chunked_merge_into_an_absent_key_or_a_hash_lands_every_chunk_with_the_expiry_by_script_and_by_transaction(bool scripts)
    {
        using var harness = Harness(layered: false);
        var cache = (global::UiPath.Caching.Redis.RedisHashCache)harness.Hashes();
        cache.ChunkScriptsEnabled = scripts;
        var migration = Migration(cache);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = raw.GetDatabase();
        var (absent, held) = (Key(), Key());
        await cache.SetAsync(held, new Dictionary<string, string?> { ["old"] = "x" }, Replace, null, Ct);
        var fields = Enumerable.Range(0, 2100).ToDictionary(i => $"f{i}", i => (string?)"y");
        var options = Replace with { SetOption = HashCacheSetOption.HashReplace, TimeToLive = TimeSpan.FromHours(2) };

        (await migration.WriteAsync(absent, fields, options, null, Ct)).Should().Be(StoreOutcome.Committed);
        (await migration.WriteAsync(held, fields, options, null, Ct)).Should().Be(StoreOutcome.Committed);

        (await db.HashLengthAsync(Find(raw, absent))).Should().BeGreaterThanOrEqualTo(2100);
        (await db.HashLengthAsync(Find(raw, held))).Should().BeGreaterThanOrEqualTo(2101);
        (await db.KeyTimeToLiveAsync(Find(raw, held)))!.Value.Should().BeCloseTo(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1));
        (await db.KeyTimeToLiveAsync(Find(raw, absent)))!.Value.Should().BeCloseTo(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task On_a_layered_cache_a_snapshot_never_reads_the_local_copy()
    {
        using var harness = Harness(layered: true);
        var cache = harness.Hashes(layered: true);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var key = Key();
        await cache.SetAsync(key, new Dictionary<string, string?> { ["a"] = "x" }, Replace, null, Ct);
        (await cache.GetAsync<string>(key, Ct)).Should().ContainKey("a");
        await raw.GetDatabase().HashSetAsync(Find(raw, key), "a", "\"changed\"");

        (await Migration(cache).ReadSnapshotAsync<string>(key, null, Ct)).Entry!.Value!["a"].Should().Be("changed");
        (await cache.GetAsync<string>(key, Ct))["a"].Should().Be("x", "the local copy is what an ordinary read answers");
    }

    [Fact]
    public async Task On_a_layered_cache_a_write_a_delete_a_field_delete_and_a_refresh_drop_the_local_copy_and_report_the_stores_outcome()
    {
        using var harness = Harness(layered: true);
        var cache = harness.Hashes(layered: true);
        var migration = Migration(cache);
        var key = Key();

        await cache.SetAsync(key, new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y" }, Replace, null, Ct);
        (await cache.GetAsync<string>(key, Ct)).Should().HaveCount(2);

        (await migration.WriteAsync(key, new Dictionary<string, string?> { ["c"] = "z" }, Replace with { SetOption = HashCacheSetOption.HashReplace }, null, Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.GetAsync<string>(key, Ct)).Should().HaveCount(3, "the local copy was dropped, so the read sees the merge");

        (await migration.DeleteFieldsAsync<string>(key, ["a"], Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.GetAsync<string>(key, Ct)).Should().NotContainKey("a");

        (await migration.RenewAsync<string>(key, new HashCacheEntryOptions(TimeToLive: TimeSpan.FromHours(2)), null, Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.TimeToLiveAsync<string>(key, Ct))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(1));

        (await migration.DeleteAsync<string>(key, Ct)).Should().Be(StoreOutcome.Committed);
        (await cache.GetAsync<string>(key, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_layered_write_that_is_cancelled_before_it_starts_is_not_sent()
    {
        using var harness = Harness(layered: true);
        var migration = Migration(harness.Hashes(layered: true));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        (await migration.WriteAsync(Key(), new Dictionary<string, string?> { ["a"] = "x" }, Replace, null, cancelled.Token)).Should().Be(StoreOutcome.NotSent);
        (await migration.DeleteAsync<string>(Key(), cancelled.Token)).Should().Be(StoreOutcome.NotSent);
    }

    private static string Key() => $"hmc:{RunTag}:{Guid.NewGuid():N}";

    private static bool Reached(TimeSpan? ttl, string expiry) => expiry == "persistent" ? ttl is null : ttl is { } left && left > TimeSpan.FromHours(1);

    private static IMigrationHashCache Migration(IHashCache cache) => (IMigrationHashCache)cache;

    private static RedisKey Find(ConnectionMultiplexer raw, string key) => raw.GetServer(raw.GetEndPoints()[0]).Keys(pattern: $"*{key}*").Single();

    private static async Task<Dictionary<string, string>> FieldsAsync(IDatabase db, RedisKey key) =>
        (await db.HashGetAllAsync(key)).ToDictionary(e => ((string?)e.Name)!, e => ((string?)e.Value)!);

    private static async Task<Dictionary<string, string>> StoredAsync(ConnectionMultiplexer raw, string key) =>
        (await raw.GetDatabase().HashGetAllAsync(Find(raw, key))).ToDictionary(e => ((string?)e.Name)!, e => ((string?)e.Value)!);

    // The library's own overload, which fires and forgets by default: the effect is waited for, and the renewed hash is the same as the one it left.
    private static async Task AssertLibraryOverloadAgreesAsync(IHashCache cache, IDatabase db, string byLibrary, (RedisKey Library, RedisKey Renewed) keys, DateTimeOffset deadline, string expiry)
    {
        await cache.RefreshAsync<string>(byLibrary, deadline, null, Ct);
        await db.PingAsync();
        Reached(await db.KeyTimeToLiveAsync(keys.Library), expiry).Should().BeTrue();

        (await FieldsAsync(db, keys.Renewed)).Should().BeEquivalentTo(await FieldsAsync(db, keys.Library), "the same fields, the metadata included");
        (await db.KeyTimeToLiveAsync(keys.Library) is null).Should().Be(expiry == "persistent");
    }

    private MigrateOnReadHarness Harness(bool layered, bool cacheNulls = false)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var raw = ConnectionMultiplexer.Connect(fixture.ConnectionString);
        _time ??= new(new DateTimeOffset(DateTime.SpecifyKind(raw.GetServer(raw.GetEndPoints()[0]).Time(), DateTimeKind.Utc)));
        return MigrateOnReadHarness.Build(fixture.ConnectionString, FormatNegotiationMode.Force, $"hm{Guid.NewGuid():N}"[..10], layered, cacheNulls: cacheNulls, clock: Time);
    }
}
