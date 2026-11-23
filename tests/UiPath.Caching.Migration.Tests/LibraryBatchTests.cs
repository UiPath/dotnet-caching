using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class LibraryBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_restore_original_names_and_pad_a_short_reply_with_misses()
    {
        var (cache, script) = ScriptedCache.Create();
        var policy = WriteFormats.Envelope;
        var path = new LibraryPathClient<Item>(new FakeMigrationCache(), new FakeConnection(), key => $"mapped:{key}", () => TimeSpan.FromSeconds(3), readPolicy: () => policy) { Cache = cache };
        var batch = path.PlainBatch ?? throw new InvalidOperationException("Missing library batch.");
        path.PlainBatch.Should().BeSameAs(batch);

        script.Answers["GetAsync"] = args =>
        {
            args[1].Should().BeSameAs(policy);
            var keys = (CacheKey[])args[0]!;
            keys.Select(key => key.Name).Should().OnlyContain(key => key.StartsWith("mapped:", StringComparison.Ordinal));
            return keys.Take(1).Select(key => new KeyValuePair<CacheKey, Item?>(key, new Item("stored"))).ToArray();
        };

        (await batch.GetAsync([], Ct)).Should().BeEmpty();
        (await batch.GetAsync(["single"], Ct)).Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, Item?>("single", new Item("stored")));
        (await batch.GetAsync(["first", "missing", "first"], Ct)).Should().Equal(
            new KeyValuePair<string, Item?>("first", new Item("stored")), new KeyValuePair<string, Item?>("missing", null), new KeyValuePair<string, Item?>("first", null));
    }

    [Fact]
    public async Task Writes_forward_mapped_keys_null_values_policy_and_deadline_and_removes_report_the_library_result()
    {
        var (cache, script) = ScriptedCache.Create();
        var policy = WriteFormats.Envelope;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        var batch = new LibraryBatch<Item>(cache, key => $"mapped:{key}", () => new WriteLease(policy, null), null);
        var seen = new List<DateTimeOffset>();
        script.Answers["SetAsync"] = args =>
        {
            var entries = (KeyValuePair<CacheKey, Item?>[])args[0]!;
            seen.Add((DateTimeOffset)args[1]!);
            args[2].Should().BeSameAs(policy);
            if (entries.Length > 0)
            {
                entries.Select(pair => pair.Key.Name).Should().Equal("mapped:item", "mapped:null");
                entries.Select(pair => pair.Value).Should().Equal(new Item("stored"), null);
            }

            return entries.Length > 0;
        };
        script.Answers["RemoveAsync"] = args =>
        {
            ((CacheKey[])args[0]!).Select(key => key.Name).Should().Equal("mapped:item");
            return false;
        };

        (await batch.SetAsync([], null, Ct)).Should().BeFalse();
        (await batch.SetAsync([new("item", new Item("stored")), new("null", null)], deadline, Ct)).Should().BeTrue();
        seen.Should().Equal(DateTimeOffset.MaxValue, deadline);
        (await batch.RemoveAsync(["item"], Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Get_or_add_selects_all_expiry_overloads_and_forwards_cancellation()
    {
        var (cache, script) = ScriptedCache.Create();
        var batch = new LibraryBatch<Item>(cache, key => $"mapped:{key}", null, null);
        var relative = TimeSpan.FromMinutes(5);
        var deadline = DateTimeOffset.UtcNow + relative;
        var seen = new List<object?>();
        script.Answers["GetOrAddAsync"] = args =>
        {
            var entries = (KeyValuePair<CacheKey, string>[])args[0]!;
            var factory = (Func<string[], CancellationToken, Task<KeyValuePair<string, Item?>[]>>)args[1]!;
            seen.Add(args[2]);
            var token = (CancellationToken)args[^1]!;
            foreach (var entry in entries)
            {
                entry.Key.Name.Should().Be($"mapped:{entry.Value}");
            }

            return token.IsCancellationRequested
                ? Task.FromCanceled<KeyValuePair<string, Item?>[]>(token)
                : factory(entries.Select(entry => entry.Value).ToArray(), token);
        };

        (await batch.GetOrAddAsync<string>([], Generate, default, Ct)).Should().BeEmpty();
        (await batch.GetOrAddAsync<string>([new("second", "second")], Generate, CacheExpiry.After(relative), Ct))
            .Should().ContainSingle().Which.Value.Should().Be(new Item("second"));
        (await batch.GetOrAddAsync<string>([new("third", "third"), new("fourth", "fourth")], Generate, CacheExpiry.At(deadline), Ct))
            .Should().Equal(new KeyValuePair<string, Item?>("third", new Item("third")), new KeyValuePair<string, Item?>("fourth", new Item("fourth")));
        seen.Should().Equal(null, relative, deadline);
        var cancelled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await batch.GetOrAddAsync<string>([new("third", "third")], Generate, default, cancelled));

        static Task<KeyValuePair<string, Item?>[]> Generate(string[] states, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(states.Select(state => new KeyValuePair<string, Item?>(state, new Item(state))).ToArray());
        }
    }
}
