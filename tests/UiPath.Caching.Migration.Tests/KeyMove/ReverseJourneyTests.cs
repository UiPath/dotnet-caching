using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A reverse after a partial and after a full forward drain, with writes and deletes between the flips: every key reads its newest value at every step, and ends in L with N empty.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class ReverseJourneyTests(RedisFixture fixture)
{
    private static readonly string[] Keys = ["k0", "k1", "k2", "k3", "k4", "k5"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reverse_after_a_partial_forward_drain_keeps_every_key_and_ends_in_L()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var client = kit.Kit.Client;
        await kit.SeedAsync(Keys.ToDictionary(k => k, k => ("v-" + k, (TimeSpan?)null)));
        var expected = Keys.ToDictionary(k => k, k => (string?)("v-" + k));

        // Forward S3: two keys promoted by reads, one written, one deleted; the rest stay in L.
        await ReadAllAsync(client, expected, "k0", "k1");
        await SetAsync(client, expected, "k2", "w2");
        await RemoveAsync(client, expected, "k3");
        (await kit.Kit.ExistsAsync(kit.Kit.N("k0"))).Should().BeTrue();
        (await kit.Kit.ExistsAsync(kit.Kit.L("k4"))).Should().BeTrue("a key no read or write touched stays in L");

        // S1(rev): Src is N; reads take N first and promote nothing, a write lands on N and deletes L, a delete clears both.
        await kit.Kit.SetStateAsync("S1", direction: "rev");
        await ExpectAsync(client, expected);
        (await kit.Kit.ExistsAsync(kit.Kit.L("k0"))).Should().BeFalse("an S1(rev) read leaves the N-only key in N");
        await SetAsync(client, expected, "k4", "w4");
        await RemoveAsync(client, expected, "k1");
        await ExpectAsync(client, expected);

        // S3(rev): reads promote back, the reverse mover moves the rest, and a write lands on L.
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await ReadAllAsync(client, expected, "k0");
        await kit.RunOnePassAsync(kit.Host(state: () => kit.State(direction: "rev")), "rev");
        await ExpectLayoutAsync(kit.Kit, expected);
        await SetAsync(client, expected, "k5", "w5");
        await ExpectAsync(client, expected);

        // S4(rev): nothing is left in N, and every key reads from L.
        await kit.Kit.SetStateAsync("S4", direction: "rev");
        await ExpectAsync(client, expected);
        await ExpectLayoutAsync(kit.Kit, expected);
    }

    [Fact]
    public async Task A_reverse_after_a_full_forward_drain_copies_all_of_N_back_and_loses_no_write()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var client = kit.Kit.Client;
        await kit.SeedAsync(Keys.ToDictionary(k => k, k => ("v-" + k, (TimeSpan?)null)));
        var expected = Keys.ToDictionary(k => k, k => (string?)("v-" + k));

        // The forward mover drains L, and S4 writes N only.
        await kit.RunOnePassAsync(kit.Host());
        foreach (var key in Keys)
        {
            (await kit.Kit.ExistsAsync(kit.Kit.L(key))).Should().BeFalse($"the forward mover drained {key}");
            (await kit.Kit.ExistsAsync(kit.Kit.N(key))).Should().BeTrue($"the forward mover copied {key} to N");
        }

        await kit.Kit.SetStateAsync("S4");
        await SetAsync(client, expected, "k0", "w0");
        await RemoveAsync(client, expected, "k1");
        await ExpectAsync(client, expected);

        await kit.Kit.SetStateAsync("S1", direction: "rev");
        await SetAsync(client, expected, "k2", "w2");
        await ExpectAsync(client, expected);

        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await RemoveAsync(client, expected, "k3");
        await kit.RunOnePassAsync(kit.Host(state: () => kit.State(direction: "rev")), "rev");
        await ExpectLayoutAsync(kit.Kit, expected);
        await SetAsync(client, expected, "k4", "w4");
        await ExpectAsync(client, expected);

        await kit.Kit.SetStateAsync("S4", direction: "rev");
        await ExpectAsync(client, expected);
        await ExpectLayoutAsync(kit.Kit, expected);
    }

    [Fact]
    public async Task A_hash_cache_reversed_after_a_partial_drain_keeps_every_field_and_ends_in_L()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        var client = kit.Client;
        await kit.Kit.PrepareAsync(Keys);
        await kit.Kit.SetStateAsync("S3");
        var expected = new Dictionary<string, Dictionary<string, string>>();
        foreach (var key in Keys)
        {
            expected[key] = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
            await kit.SetAsync(kit.L(key), expected[key]);
        }

        // Forward S3: two hashes promoted by reads, a merge into one, a field delete from another.
        await HashReadAsync(client, "k0", expected["k0"]);
        await HashReadAsync(client, "k1", expected["k1"]);
        await MergeAsync(client, expected, "k2", "c", "3");
        await RemoveFieldAsync(client, expected, "k3", "a");
        foreach (var (key, fields) in expected)
        {
            var moved = key is "k0" or "k1" or "k2";
            var holder = moved ? kit.N(key) : kit.L(key);
            var other = moved ? kit.L(key) : kit.N(key);
            (await kit.FieldsAsync(holder)).Should().BeEquivalentTo(fields, $"{holder} holds the current fields");
            (await kit.Kit.ExistsAsync(other)).Should().BeFalse($"{holder} is the only copy");
        }

        // S1(rev): field writes are refused there, while a whole replace and a key delete run.
        await kit.Kit.SetStateAsync("S1", direction: "rev");
        await ExpectHashesAsync(client, expected);
        (await client.SetAsync("k4", KeyMoveHashKit.Items(("z", "9")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        await ReplaceAsync(client, expected, "k4", "x", "8");
        (await client.RemoveAsync("k5", Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        expected.Remove("k5");
        await ExpectHashesAsync(client, expected);

        // S3(rev): reads and merges promote what N holds. Field deletes clear the named fields directly in N and L.
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await MergeAsync(client, expected, "k0", "d", "4");
        await RemoveFieldAsync(client, expected, "k1", "b");
        foreach (var key in Keys)
        {
            await HashReadAsync(client, key, expected.GetValueOrDefault(key));
        }

        await ExpectHashesAsync(client, expected);

        await kit.Kit.SetStateAsync("S4", direction: "rev");
        await ExpectHashesAsync(client, expected);
        foreach (var key in Keys)
        {
            (await kit.Kit.ExistsAsync(kit.N(key))).Should().BeFalse($"N holds nothing of {key} at the end");
            if (expected.TryGetValue(key, out var fields))
            {
                (await kit.FieldsAsync(kit.L(key))).Should().BeEquivalentTo(fields, $"{key} ends in L");
            }
            else
            {
                (await kit.Kit.ExistsAsync(kit.L(key))).Should().BeFalse();
            }
        }
    }

    private static async Task ReadAllAsync(KeyMoveClient<Item> client, Dictionary<string, string?> expected, params string[] keys)
    {
        foreach (var key in keys)
        {
            var read = await client.ReadAsync(key, Ct);
            read.Kind.Should().Be(ReadOutcome.Found, $"{key} should be present");
            read.Value.Should().NotBeNull();
            read.Value.Name.Should().Be(expected[key]);
        }
    }

    private static async Task SetAsync(KeyMoveClient<Item> client, Dictionary<string, string?> expected, string key, string value)
    {
        (await client.SetAsync(key, new Item(value), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        expected[key] = value;
    }

    private static async Task RemoveAsync(KeyMoveClient<Item> client, Dictionary<string, string?> expected, string key)
    {
        (await client.RemoveAsync(key, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        expected[key] = null;
    }

    private static async Task ExpectAsync(KeyMoveClient<Item> client, Dictionary<string, string?> expected)
    {
        foreach (var (key, value) in expected)
        {
            var read = await client.ReadAsync(key, Ct);
            if (value is null)
            {
                read.Kind.Should().Be(ReadOutcome.Absent, $"{key} was deleted");
            }
            else
            {
                read.Value!.Name.Should().Be(value, $"{key} reads its newest value");
            }
        }
    }

    private static async Task ExpectLayoutAsync(KeyMoveKit kit, Dictionary<string, string?> expected)
    {
        foreach (var (key, value) in expected)
        {
            (await kit.ExistsAsync(kit.N(key))).Should().BeFalse($"N holds nothing of {key} after the move");
            (await kit.GetAsync(kit.L(key))).Should().Be(value, $"L holds the expected value of {key}");
        }
    }

    private static async Task HashReadAsync(KeyMoveHashClient<Item> client, string key, Dictionary<string, string>? expected)
    {
        var read = await client.ReadAsync(key, null, Ct);
        if (expected is null)
        {
            read.Kind.Should().Be(ReadOutcome.Absent, $"{key} was deleted");
            return;
        }

        read.Kind.Should().Be(ReadOutcome.Found, $"{key} should be present");
        read.Fields!.ToDictionary(f => f.Key, f => f.Value!.Name).Should().BeEquivalentTo(expected, $"{key} reads its newest fields");
    }

    private static async Task MergeAsync(KeyMoveHashClient<Item> client, Dictionary<string, Dictionary<string, string>> expected, string key, string field, string value)
    {
        (await client.SetAsync(key, KeyMoveHashKit.Items((field, value)), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        expected[key][field] = value;
    }

    private static async Task ReplaceAsync(KeyMoveHashClient<Item> client, Dictionary<string, Dictionary<string, string>> expected, string key, string field, string value)
    {
        (await client.SetAsync(key, KeyMoveHashKit.Items((field, value)), new HashWriteOptions(HashCacheSetOption.KeyReplace, null), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        expected[key] = new Dictionary<string, string> { [field] = value };
    }

    private static async Task RemoveFieldAsync(KeyMoveHashClient<Item> client, Dictionary<string, Dictionary<string, string>> expected, string key, string field)
    {
        (await client.RemoveFieldsAsync(key, [field], Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        expected[key].Remove(field);
    }

    private static async Task ExpectHashesAsync(KeyMoveHashClient<Item> client, Dictionary<string, Dictionary<string, string>> expected)
    {
        foreach (var key in Keys)
        {
            await HashReadAsync(client, key, expected.GetValueOrDefault(key));
        }
    }

    private static HashWriteOptions Merge() => new(HashCacheSetOption.HashReplace, null);
}
