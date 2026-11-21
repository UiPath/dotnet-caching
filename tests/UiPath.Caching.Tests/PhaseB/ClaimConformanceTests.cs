using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

/// <summary>T-S3-12: one table of (record, calls, codes, final record), run through the Lua scripts and through the C# fallback, which must agree.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class ClaimConformanceTests(RedisContainerFixture fixture)
{
    private const string T1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string T2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Fmt = "fmt:1>2";
    private static readonly string Group201 = new('g', 201);
    private static readonly string Ptr1 = $"g1|{T1}|2|1";

    public static TheoryData<string> Rows => [.. Table.Keys];

    private static Dictionary<string, Row> Table { get; } = new Row[]
    {
        new("status bogus is the own field malformed", [("rt:a", $"bogus|{T1}|rt|")], [Pend("rt:a", T1, "rt", "", -9)]),
        new("status bogus elsewhere refuses another acquisition", [("rt:a", $"bogus|{T1}|rt|")], [Pend("rt:b", T1, "rt", "", -4)]),
        new("a 31 character token", [("rt:a", $"pend|{T1[..31]}|rt|")], [Pend("rt:b", T1, "rt", "", -4)]),
        new("an empty descriptor", [("rt:a", $"pend|{T1}||")], [Pend("rt:b", T1, "rt", "", -4)]),
        new("a trailing newline", [("rt:a", $"pend|{T1}|rt|\n")], [Pend("rt:b", T1, "rt", "", -4)]),
        new("a field with a space", [("rt:a b", $"pend|{T1}|rt|")], [Pend("rt:x", T1, "rt", "", -4), Pend("rt:a b", T1, "rt", "", -8)]),
        new("a group with an underscore", [], [Pend("rt:a_b", T1, "rt", "", 1)], [("rt:a_b", $"pend|{T1}|rt|")]),
        new("a 201 character group", [], [Pend("rt:" + Group201, T1, "rt", "", -8)]),
        new("ids of 8 and 9 characters", [], [Pend("fm:g", T1, "fmt:12345678>1", "-", 1), Pend("fm:h", T2, "fmt:123456789>1", "-", -8)], [("fm:g", $"pend|{T1}|fmt:12345678>1|")]),
        new("a 19 digit epoch", [("rt:a", $"open|{T1}|rt|")], [Rel("rt:a", T1, "cert", "1234567890123456789", "S0", -8)]),
        new("a cert 5:S3", [("rt:a", $"open|{T1}|rt|5:S3")], [Pend("rt:b", T1, "rt", "", -4)]),
        new("a field name outside the grammar", [("xx:a", $"pend|{T1}|rt|")], [Pend("rt:b", T1, "rt", "", -4)]),
        new("a malformed pointer", [("cur:fm", "garbage"), ("fm:g1", $"open|{T1}|{Fmt}|7:S0")], [Pend("rt:b", T1, "rt", "", -4), Rel("fm:g1", T1, "idle", "", "", -9)]),
        new("PEND on an own closed field", [("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), ("cur:fm", Ptr1)], [Pend("fm:g1", T2, Fmt, Ptr1, 0)]),
        new("PEND of another group beside a closed one", [("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), ("cur:fm", Ptr1)], [Pend("fm:g2", T2, Fmt, Ptr1, 1)], [("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), ("cur:fm", Ptr1), ("fm:g2", $"pend|{T2}|{Fmt}|")]),
        new("PEND of another group beside an open one", [("fm:g1", $"open|{T1}|{Fmt}|")], [Pend("fm:g2", T2, Fmt, "-", -3)]),
        new("PEND deletes idle fields of other groups of its kind", [("fm:g1", $"idle|{T1}|{Fmt}|3:S0")], [Pend("fm:g2", T2, Fmt, "-", 1)], [("fm:g2", $"pend|{T2}|{Fmt}|")]),
        new("PEND of another kind while one is live", [("rt:r", $"open|{T1}|rt|")], [Pend("fm:g", T2, Fmt, "-", -4)]),
        new("rt groups coexist", [("rt:r1", $"open|{T1}|rt|")], [Pend("rt:r2", T2, "rt", "", 1)], [("rt:r1", $"open|{T1}|rt|"), ("rt:r2", $"pend|{T2}|rt|")]),
        new("a spent token", [("rt:a", $"idle|{T1}|rt|")], [Pend("rt:a", T1, "rt", "", -6)]),
        new("PEND then OPEN then a repeat", [], [Pend("rt:a", T1, "rt", "", 1), Pend("rt:a", T1, "rt", "", 2), Open("rt:a", T1, "rt", false, 1), Open("rt:a", T1, "rt", false, 2)], [("rt:a", $"open|{T1}|rt|")]),
        new("OPEN restore over closed", [("rt:a", $"closed|{T1}|rt|4:S5")], [Open("rt:a", T2, "rt", true, 0)]),
        new("OPEN restore over absent", [], [Open("rt:a", T1, "rt", true, 1), Open("rt:b", T2, "rt", false, 0)], [("rt:a", $"open|{T1}|rt|")]),
        new("REL idle with an S5 cert", [("rt:a", $"open|{T1}|rt|8:S5")], [Rel("rt:a", T1, "idle", "", "", 0)]),
        new("REL closed with an S0 cert", [("rt:a", $"open|{T1}|rt|8:S0")], [Rel("rt:a", T1, "closed", "", "", 0)]),
        new("REL cert with an empty epoch", [("rt:a", $"open|{T1}|rt|")], [Rel("rt:a", T1, "cert", "", "S0", -8)]),
        new("REL idle of a pend clears to idle with no cert", [("rt:a", $"pend|{T1}|rt|")], [Rel("rt:a", T1, "idle", "", "", 1), Rel("rt:a", T1, "idle", "", "", 2)], [("rt:a", $"idle|{T1}|rt|")]),
        new("REOPEN while another group is live", [("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), ("cur:fm", Ptr1), ("fm:g2", $"pend|{T2}|{Fmt}|")], [Reopen("fm:g1", T1, -3)]),
        new(
            "REOPEN then idle restores the pointer it names",
            [("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), ("cur:fm", Ptr1), ("prv:g1", "-")],
            [Reopen("fm:g1", T1, 1), Rel("fm:g1", T1, "cert", "12", "S0", 1), Rel("fm:g1", T1, "idle", "", "", 1)],
            [("fm:g1", $"idle|{T1}|{Fmt}|12:S0")]),
        new(
            "REL closed twice keeps the pointer and prv",
            [("fm:g1", $"open|{T1}|{Fmt}|9:S5")],
            [Rel("fm:g1", T1, "closed", "", "", 1), Rel("fm:g1", T1, "closed", "", "", 2)],
            [("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), ("cur:fm", Ptr1), ("prv:g1", "-")]),
        new("DROP of the pointer", [("cur:fm", Ptr1)], [Drop("cur:fm", Ptr1, "n", -8)], [("cur:fm", Ptr1)]),
        new("DROP of a closed field", [("rt:a", $"closed|{T1}|rt|4:S5")], [Drop("rt:a", $"closed|{T1}|rt|4:S5", "n", 1), Drop("rt:a", "x", "n", 2)], []),
        new("DROP of a changed value", [("rt:a", $"open|{T1}|rt|")], [Drop("rt:a", $"pend|{T1}|rt|", "n", 0)], [("rt:a", $"open|{T1}|rt|")]),
        new("PEND fm with a stale expected pointer", [("cur:fm", Ptr1)], [Pend("fm:g2", T2, Fmt, "-", -5)], [("cur:fm", Ptr1)]),
        new("PEND fm with - against an absent pointer", [], [Pend("fm:g2", T2, Fmt, "-", 1)], [("fm:g2", $"pend|{T2}|{Fmt}|")]),
        new("REOPEN of an fm group the pointer does not name", [("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), ("cur:fm", $"g0|{T2}|2|1")], [Reopen("fm:g1", T1, 0)]),
        new(
            "the stale gate of MC4",
            [("fm:g1", $"closed|{T1}|{Fmt}|9:S5"), ("cur:fm", Ptr1), ("prv:g1", $"g0|{T2}|1|0")],
            [Reopen("fm:g1", T1, 1), Rel("fm:g1", T1, "cert", "13", "S0", 1), Rel("fm:g1", T1, "idle", "", "", 1), Pend("fm:g2", T2, Fmt, Ptr1, -5)],
            [("fm:g1", $"idle|{T1}|{Fmt}|13:S0"), ("cur:fm", $"g0|{T2}|1|0")]),
    }.ToDictionary(r => r.Name);

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task The_scripts_and_the_fallback_give_the_same_codes_and_record(string row)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        await using var mux = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var db = mux.GetDatabase();
        foreach (var scripts in new[] { true, false })
        {
            var key = (RedisKey)$"claimtest:mx:{{{Guid.NewGuid():N}}}";
            try
            {
                var store = new ClaimStore(db, key, scripts, fixture.ServerVersion);
                var spec = Table[row];
                foreach (var (field, value) in spec.Record)
                {
                    await db.HashSetAsync(key, field, value);
                }

                var codes = new List<long>();
                foreach (var step in spec.Steps)
                {
                    codes.Add(await step.Run(store));
                }

                codes.Should().Equal(spec.Steps.Select(s => s.Expected), $"{(scripts ? "Lua" : "fallback")}: {row}");
                var final = (await db.HashGetAllAsync(key)).ToDictionary(e => (string)e.Name!, e => (string)e.Value!);
                var expected = (spec.Final ?? spec.Record).ToDictionary(p => p.Field, p => p.Value);
                final.Should().BeEquivalentTo(expected, $"{(scripts ? "Lua" : "fallback")}: {row}");
            }
            finally
            {
                await db.KeyDeleteAsync(key);
            }
        }
    }

    private static Step Pend(string field, string token, string descriptor, string pointer, long expected) =>
        new(store => store.PendAsync(field, token, descriptor, pointer), expected);

    private static Step Open(string field, string token, string descriptor, bool restore, long expected) =>
        new(async store => (await store.OpenAsync(field, token, descriptor, restore)).Code, expected);

    private static Step Rel(string field, string token, string target, string epoch, string terminal, long expected) =>
        new(async store => (await store.ReleaseAsync(field, token, target, epoch, terminal)).Code, expected);

    private static Step Reopen(string field, string token, long expected) =>
        new(async store => (await store.ReopenAsync(field, token)).Code, expected);

    private static Step Drop(string field, string value, string confirm, long expected) =>
        new(store => store.DropAsync(field, value, confirm), expected);

    private sealed record Step(Func<ClaimStore, Task<long>> Run, long Expected);

    private sealed record Row(string Name, (string Field, string Value)[] Record, Step[] Steps, (string Field, string Value)[]? Final = null);
}
