using UiPath.Caching.KeyMove;
using static UiPath.Caching.Tests.KeyMove.PlannerKit;

namespace UiPath.Caching.Tests.KeyMove;

// The registration traits as the driver reads them: NoOrigin takes the S2 barrier, AcceptRefill opens X11, and the source life bounds the soak.
public class RegisteredTraitsTests
{
    internal const string Library = "lib:";

    private static readonly string ReportId = new DryRunStamp(DateTimeOffset.UnixEpoch.AddDays(29), 1_000, "ok").ReportId;

    [Fact]
    public async Task A_NoOrigin_cache_among_the_managed_ones_makes_the_preparing_group_take_X2_and_without_one_it_takes_X3()
    {
        var state = State("S1", ep: 11, caches: "a+b", prev: "S0:none:0:10:id10");
        var fresh = Fleet(state);

        var withNoOrigin = await StepPlanner.DecideAsync(Input(state, fresh, facts: Facts(noOrigin: "b")), Defaults);
        var without = await StepPlanner.DecideAsync(Input(state, fresh, facts: Facts()), Defaults);
        var unmanaged = await StepPlanner.DecideAsync(Input(state with { Caches = "a", CacheSet = StateRecord.HashOfCaches("a") }, Fleet(state with { Caches = "a" }), facts: Facts(noOrigin: "b")), Defaults);

        withNoOrigin.Spec!.Code.Should().Be("X2");
        without.Spec!.Code.Should().Be("X3");
        unmanaged.Spec!.Code.Should().Be("X3", "a cache outside the stored set does not take part");
    }

    [Fact]
    public async Task X11_opens_only_when_every_managed_cache_accepts_a_refill()
    {
        var done = State("S5", ep: 20, nPop: 1, caches: "a+b", prev: "S4:fwd:1:19:id19").Approved(ApprovalKinds.Abandon);

        (await StepPlanner.DecideAsync(Input(done, facts: Facts(refill: ["a", "b"])), Defaults)).Kind.Should().Be(StepKind.ColdStart);
        var refused = await StepPlanner.DecideAsync(Input(done, facts: Facts(refill: ["a"])), Defaults);
        refused.Kind.Should().Be(StepKind.None);
        refused.AlertList.Should().Contain(new StageAlert("abandon_refused", "a cache does not accept a refill"));
    }

    [Fact]
    public void The_soak_is_bounded_by_the_longest_source_life_only_when_every_cache_states_one()
    {
        var all = new RegisteredGroupFacts([Cache("a", ttl: TimeSpan.FromHours(2)), Cache("b", ttl: TimeSpan.FromHours(5))], false);
        var partial = new RegisteredGroupFacts([Cache("a", ttl: TimeSpan.FromHours(2)), Cache("b")], false);

        all.LongestSourceTtl("g1").Should().Be(TimeSpan.FromHours(5));
        partial.LongestSourceTtl("g1").Should().BeNull();
        all.LongestSourceTtl("other").Should().BeNull();
    }

    [Fact]
    public void The_floor_attestation_and_the_defaults_of_the_facts_no_registration_holds()
    {
        var facts = new RegisteredGroupFacts([Cache("a")], true);

        facts.ReleaseFloorAttested.Should().BeTrue();
        new RegisteredGroupFacts([Cache("a")], false).ReleaseFloorAttested.Should().BeFalse();
        facts.TakesClaim("g1").Should().BeTrue("a router group takes the claim");
        facts.ClaimWired("g1").Should().BeFalse("until a real claim is wired");
        facts.AllAcceptRefill("g1", []).Should().BeFalse("no cache, nothing to accept");
    }

    [Fact]
    public async Task Start_is_refused_for_each_fact_the_app_has_not_attested_for_the_group()
    {
        var cache = new[] { Cache("a") };
        var on = Attested();
        (await StepPlanner.DecideAsync(Input(ReadyOff(), facts: Wired(cache, on, on, Memory(ReportId))), Defaults)).Kind.Should().Be(StepKind.Transit, "every fact attested");

        await Refused(Wired(cache, null, on, Memory(ReportId)), KeyMoveGateIds.StartFormatSettled);
        await Refused(Wired(cache, on, null, Memory(ReportId)), KeyMoveGateIds.StartNodeList);
        await Refused(Wired(cache, on, on, null), KeyMoveGateIds.StartMemoryHeadroom);
        await Refused(new RegisteredGroupFacts(cache, true, claimWired: true), KeyMoveGateIds.StartFormatSettled);
    }

    [Fact]
    public async Task An_attestation_covers_its_own_group_and_its_own_dry_run_report_only()
    {
        var cache = new[] { Cache("a") };
        var other = new Dictionary<string, string> { ["another"] = "e" };

        await Refused(Wired(cache, other, Attested(), Memory(ReportId)), KeyMoveGateIds.StartFormatSettled);
        await Refused(Wired(cache, Attested(), other, Memory(ReportId)), KeyMoveGateIds.StartNodeList);
        await Refused(Wired(cache, Attested(), Attested(), Memory("an-older-report")), KeyMoveGateIds.StartMemoryHeadroom);
        await Refused(Wired(cache, Attested(), Attested(), new Dictionary<string, string> { ["another"] = ReportId }), KeyMoveGateIds.StartMemoryHeadroom);
    }

    [Fact]
    public void An_evidence_id_serves_one_move_a_Start_never_accepts_a_consumed_one_and_the_move_itself_needs_it()
    {
        var facts = Wired([Cache("a")], Attested(), Attested(), Memory(ReportId));
        var fresh = State("S0");
        var consumed = State("S0", fields: (StateFields.Attested, "f:evidence,n:evidence"));
        var running = State("S3", nPop: 1, fields: (StateFields.Attested, "f:evidence,n:evidence"));

        facts.FormatSettled("g1", fresh).Should().BeTrue();
        facts.NodeListComplete("g1", fresh).Should().BeTrue();
        facts.FormatSettled("g1", consumed).Should().BeFalse("a Start already used this evidence");
        facts.NodeListComplete("g1", consumed).Should().BeFalse();
        facts.NodeListComplete("g1", running).Should().BeTrue("the move it started still holds it, for its reverse as well");
        facts.NodeListComplete("g1", State("S3", nPop: 1)).Should().BeFalse("a move that never recorded it has no such evidence");
        facts.StartTaken("g1").Should().Equal("f:evidence", "n:evidence");
        facts.MemoryFits("g1", ReportId, 1, 0.2).Should().BeTrue("memory stays bound to its report");
    }

    [Fact]
    public void Two_instances_and_a_restart_agree_because_the_consumed_evidence_lives_in_the_state()
    {
        var state = State("S0", fields: (StateFields.Attested, "f:evidence,n:evidence"));
        var first = Wired([Cache("a")], Attested(), Attested(), Memory(ReportId));
        var restarted = Wired([Cache("a")], Attested(), Attested(), Memory(ReportId));

        first.FormatSettled("g1", state).Should().BeFalse();
        restarted.FormatSettled("g1", state).Should().BeFalse("a restart or a node that never ran X1 reads the same state");
        restarted.NodeListComplete("g1", state).Should().BeFalse();
    }

    [Fact]
    public void The_consumed_evidence_of_one_group_does_not_spend_another_groups()
    {
        var facts = Wired([Cache("a"), Cache("b", group: "g2")], new Dictionary<string, string> { ["g1"] = "e1", ["g2"] = "e1" }, Attested(), Memory(ReportId));
        var state = State("S0", fields: (StateFields.Attested, "f:e1"));

        facts.FormatSettled("g1", state).Should().BeFalse();
        facts.FormatSettled("g2", state).Should().BeFalse("the state of g2 would hold its own entries; this one is shared only for the test");
        facts.FormatSettled("g2", State("S0")).Should().BeTrue();
    }

    [Fact]
    public async Task A_group_that_takes_a_claim_cannot_start_until_a_claim_is_wired()
    {
        var on = Attested();
        var cache = new[] { Cache("a") };

        new RegisteredGroupFacts(cache, true, on, on, Memory(ReportId)).TakesClaim("g1").Should().BeTrue();
        await Refused(new RegisteredGroupFacts(cache, true, on, on, Memory(ReportId), claimWired: false), "claim_unavailable");
        (await StepPlanner.DecideAsync(Input(ReadyOff(), facts: Wired(cache, on, on, Memory(ReportId))), Defaults)).Spec!.TakeClaim.Should().BeTrue();
    }

    [Fact]
    public async Task A_move_started_with_real_facts_can_be_reversed_and_a_move_without_its_evidence_cannot()
    {
        var on = Attested();
        var facts = Wired([Cache("a")], on, on, Memory(ReportId));
        var started = State("S4", ep: 20, nPop: 1, prev: "S3:fwd:1:19:id19", fields: (StateFields.Attested, "f:evidence,n:evidence"));
        var unrecorded = State("S4", ep: 20, nPop: 1, prev: "S3:fwd:1:19:id19");

        var reverse = await StepPlanner.DecideAsync(Input(started, Fleet(started, fl: 2), Verdict(min: 2, lowered: 2), facts), Defaults);
        var blocked = await StepPlanner.DecideAsync(Input(unrecorded, Fleet(unrecorded, fl: 2), Verdict(min: 2, lowered: 2), facts), Defaults);

        reverse.Spec!.Code.Should().Be("X7");
        blocked.Kind.Should().Be(StepKind.None);
        blocked.AlertList.Should().Contain(a => a.Code == "reverse_blocked" && a.Detail.Contains(KeyMoveGateIds.StartNodeList, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stored_memory_consent_still_overrides_an_unattested_memory_plan()
    {
        var facts = Wired([Cache("a")], Attested(), Attested(), null);

        var plan = await StepPlanner.DecideAsync(Input(ReadyOff(ApprovalKinds.StartMemory), facts: facts), Defaults);

        plan.Kind.Should().Be(StepKind.Transit);
    }

    internal static KeyMoveCache Cache(string name, string group = "g1", bool noOrigin = false, bool refill = false, TimeSpan? ttl = null) =>
        new(name, group, noOrigin, false, refill, ttl, $"l:{name}:*", k => $"l:{name}:{k}", k => k.StartsWith($"l:{name}:", StringComparison.Ordinal) ? k[(3 + name.Length)..] : null, $"{Library}n:{name}:*", k => $"{Library}n:{name}:{k}", k => k.StartsWith($"{Library}n:{name}:", StringComparison.Ordinal) ? k[($"{Library}n:{name}:".Length)..] : null);

    private static async Task Refused(RegisteredGroupFacts facts, string gate)
    {
        var plan = await StepPlanner.DecideAsync(Input(ReadyOff(), facts: facts), Defaults);

        plan.Kind.Should().Be(StepKind.None);
        plan.AlertList.Should().Contain(a => a.Code == "start_blocked" && a.Detail.Contains(gate, StringComparison.Ordinal));
    }

    private static RegisteredGroupFacts Wired(KeyMoveCache[] caches, IReadOnlyDictionary<string, string>? format, IReadOnlyDictionary<string, string>? node, IReadOnlyDictionary<string, string>? memory) =>
        new(caches, true, format, node, memory, claimWired: true);

    private static Dictionary<string, string> Attested() => new() { ["g1"] = "evidence" };

    private static Dictionary<string, string> Memory(string report) => new() { ["g1"] = report };

    private static RegisteredGroupFacts Facts(string? noOrigin = null, string[]? refill = null) =>
        new([Cache("a", noOrigin: noOrigin == "a", refill: refill?.Contains("a") == true), Cache("b", noOrigin: noOrigin == "b", refill: refill?.Contains("b") == true)], true);
}
