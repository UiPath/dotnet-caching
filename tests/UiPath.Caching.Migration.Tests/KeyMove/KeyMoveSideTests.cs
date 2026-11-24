using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveSideTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("read-S1", "Snapshot")]
    [InlineData("read-S2", "Snapshot")]
    [InlineData("read-S2-NoOrigin-miss", "Snapshot,ControlId")]
    [InlineData("read-union", "Snapshot")]
    [InlineData("promotion", "Exists,Snapshot,CompareDelete")]
    [InlineData("set-S1", "Set")]
    [InlineData("set-S2", "Set")]
    [InlineData("remove-S1", "Delete")]
    [InlineData("remove-S2", "Delete")]
    [InlineData("fill-S1", "Set")]
    [InlineData("fill-lost-S2", "Set,Snapshot")]
    [InlineData("tryadd-S0", "Create")]
    [InlineData("tryadd-S1", "Set")]
    [InlineData("tryadd-S3", "Exists")]
    [InlineData("batch-S3", "Exists,Snapshot,CompareDelete")]
    public async Task Each_form_reaches_L_only_through_the_side(string form, string expected)
    {
        RecordingMoveSide? side = null;
        KeyRecordingDatabase? recorder = null;
        await using var kit = await KeyMoveKit.CreateAsync(
            fixture,
            noOrigin: form.Contains("NoOrigin", StringComparison.Ordinal),
            source: k => side = new RecordingMoveSide(new InPlaceMoveSide<Item>(k.SidePlane, k.Group, k.L, ItemCodec.Instance, null, k.Clock)),
            interceptDb: db => KeyRecordingDatabase.Over(db, out recorder));

        await RunAsync(kit, form);

        var calls = side?.Calls ?? throw new InvalidOperationException("The recording source was not created.");
        foreach (var call in expected.Split(','))
        {
            calls.Should().Contain(call, form);
        }

        var sourceKeys = SourceKeys(kit, ["k", "a", "b"]);
        var touched = recorder?.Touched ?? throw new InvalidOperationException("The database recorder was not created.");
        touched.Should().NotContain(key => sourceKeys.Contains(key), form);
    }

    [Fact]
    public async Task The_database_recorder_observes_native_keys()
    {
        KeyRecordingDatabase? recorder = null;
        await using var kit = await KeyMoveKit.CreateAsync(fixture, interceptDb: db => KeyRecordingDatabase.Over(db, out recorder));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v");

        (await kit.Client.ReadAsync("k", Ct)).Value?.Name.Should().Be("v");

        var touched = recorder?.Touched ?? throw new InvalidOperationException("The database recorder was not created.");
        touched.Should().Contain(kit.N("k"));
        (await kit.GetAsync(kit.L("k"))).Should().BeNull();
    }

    private static HashSet<string> SourceKeys(KeyMoveKit kit, string[] keys)
    {
        // The two sides share a control record outside a cluster.
        var nativeControls = keys.Select(key => kit.Names.ControlFor(kit.Group, kit.N(key)).ToString()).ToHashSet();
        return keys.SelectMany(key => new[] { kit.L(key), kit.Names.ControlFor(kit.Group, kit.L(key)).ToString() })
            .Where(key => !nativeControls.Contains(key))
            .ToHashSet();
    }

    private static Task SetStateAsync(KeyMoveKit kit, string form)
    {
        var stateIndex = form.LastIndexOf("-S", StringComparison.Ordinal);
        var state = form switch
        {
            "read-union" => "S1",
            _ when stateIndex >= 0 => form.Substring(stateIndex + 1, 2),
            _ => "S3",
        };
        var nPop = state is "S1" or "S2" && form != "read-union" ? 0 : 1;
        return kit.SetStateAsync(state, nPop);
    }

    private static async Task RunAsync(KeyMoveKit kit, string form)
    {
        await kit.PrepareAsync("k", "a", "b");
        await SetStateAsync(kit, form);
        var item = new Item("v");

        switch (form)
        {
            case "read-S1" or "read-S2" or "read-union" or "promotion":
                await kit.Db.StringSetAsync(kit.L("k"), "v");
                (await kit.Client.ReadAsync("k", Ct)).Value?.Name.Should().Be("v");
                break;
            case "read-S2-NoOrigin-miss":
                (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent);
                break;
            case "set-S1" or "set-S2":
                (await kit.Client.SetAsync("k", item, null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
                break;
            case "remove-S1" or "remove-S2":
                (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);
                break;
            case "fill-S1":
                (await kit.Client.FillAsync("k", item, null, new FillBox<Item>(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
                break;
            case "fill-lost-S2":
                await VerifyLosingFillAsync(kit, item);
                break;
            case "tryadd-S0" or "tryadd-S1" or "tryadd-S3":
                (await kit.Client.TryAddAsync("k", item, TimeSpan.FromMinutes(5), Ct)).Kind.Should().Be(TryAddKind.Created);
                break;
            default:
                await VerifyBatchAsync(kit);
                break;
        }
    }

    private static async Task VerifyLosingFillAsync(KeyMoveKit kit, Item item)
    {
        await kit.Db.StringSetAsync(kit.L("k"), "held");
        var box = new FillBox<Item>();
        (await kit.Client.FillAsync("k", item, null, box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        box.Stored?.Name.Should().Be("held");
    }

    private static async Task VerifyBatchAsync(KeyMoveKit kit)
    {
        await kit.Db.StringSetAsync(kit.L("a"), "va");
        await kit.Db.StringSetAsync(kit.L("b"), "vb");
        var read = await kit.Client.ReadManyAsync(["a", "b"], Ct);
        read["a"].Value?.Name.Should().Be("va");
        read["b"].Value?.Name.Should().Be("vb");
    }
}
