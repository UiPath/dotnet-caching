using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// States, adverts and inputs for the step logic, with every gate green unless a test changes it.
internal static class PlannerKit
{
    public const string Group = "g1";

    public static readonly StageDriverOptions Defaults = new();

    public static StateRecord State(
        string state = "S0",
        string direction = "fwd",
        long ep = 10,
        bool durable = true,
        int nPop = 0,
        string caches = "a",
        string prev = "S0:none:0:9:id9",
        string holdOp = "",
        string holdAuto = "",
        string claim = "",
        string swept = "",
        IReadOnlyDictionary<string, long>? residue = null,
        params (string Field, string Value)[] fields)
    {
        var values = fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);
        return new StateRecord(state, direction, ep, "id" + ep, durable, nPop, prev, null, caches, StateRecord.HashOfCaches(caches), string.Empty, swept, claim, string.Empty, string.Empty, holdOp, holdAuto, residue, [.. values.Keys], values);
    }

    public static StateRecord Approved(this StateRecord state, string kind, string confirm = "ok")
    {
        var approval = new Approval(kind, state.Identity, Approval.EvidenceOf(kind, state), "op", confirm);
        return state.With((StateFields.ApprovalPrefix + kind, approval.Encode()));
    }

    public static StateRecord With(this StateRecord state, params (string Field, string Value)[] fields)
    {
        var values = new Dictionary<string, string>(state.Values ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (field, value) in fields)
        {
            values[field] = value;
        }

        return state with { Values = values, FieldNames = [.. values.Keys] };
    }

    public static NodeAdvert Advert(string node, int? fl = 3, bool km = true, bool pin = true, bool kmg = true, string? ca = "a", StateRecord? acks = null, string? scope = null, int? snap = 0, int? pi = 0, long? rc = null, bool byp = false, string? group = null)
    {
        group ??= Group;
        var tokens = new List<string>();
        if (km)
        {
            tokens.Add(NodeAdvert.Km);
        }

        if (pin)
        {
            tokens.Add($"pin={group}");
        }

        if (kmg)
        {
            tokens.Add($"kmg={group}");
        }

        if (ca is not null)
        {
            tokens.Add($"ca={group}:{ca}");
        }

        if (fl is { } view)
        {
            tokens.Add(NodeAdvert.Flag(group, view));
        }

        tokens.Add(NodeAdvert.Identity(group, acks is null ? null : new AckedIdentity(acks.Ep, acks.Id, acks.State, acks.Direction)));
        AddStatusTokens(tokens, group, pi, rc, byp, scope, snap);
        return new NodeAdvert(node, TimeSpan.Zero, tokens);
    }

    public static IReadOnlyList<NodeAdvert> Fleet(StateRecord? acks = null, int? fl = 3, int count = 2) =>
        [.. Enumerable.Range(1, count).Select(n => Advert("n" + n, fl: fl, ca: acks is null ? "a" : string.Join(',', acks.CacheIds), acks: acks))];

    public static FlagVerdict Verdict(int? min = 3, int? lowered = null, int fresh = 2, bool missing = false, TimeSpan? confirmed = null) =>
        new(fresh, missing, min, lowered, confirmed ?? TimeSpan.Zero);

    public static StepInput Input(
        StateRecord state,
        IReadOnlyList<NodeAdvert>? fresh = null,
        FlagVerdict? flag = null,
        IGroupFacts? facts = null,
        TimeSpan? stable = null,
        TimeSpan? durZero = null,
        FakeGroupData? data = null,
        FakeMoverStatus? mover = null,
        PreflightResult? preflight = null,
        string? conflict = null)
    {
        facts ??= new FakeGroupFacts { ReleaseFloorAttested = true };
        data ??= new FakeGroupData();
        mover ??= new FakeMoverStatus();
        return new StepInput(
            Group,
            state,
            fresh ?? Fleet(state),
            flag ?? Verdict(),
            stable ?? TimeSpan.FromHours(3),
            DateTimeOffset.UnixEpoch.AddDays(30),
            durZero,
            conflict,
            null,
            TimeSpan.FromSeconds(60),
            facts,
            _ => Task.FromResult(preflight ?? new PreflightResult([])),
            () => data.CountNativeAsync(Group, CancellationToken.None),
            () => mover.DrainAsync(Group, state.Direction, CancellationToken.None),
            () => mover.ResidueInventoryAsync(Group, CancellationToken.None));
    }

    // The S0 state with a recent dry run, so every start gate holds.
    public static StateRecord ReadyOff(params string[] approvals)
    {
        var state = State(fields: (StateFields.DryRun, new DryRunStamp(DateTimeOffset.UnixEpoch.AddDays(29), 1_000, "ok").Encode()));
        foreach (var kind in approvals)
        {
            state = state.Approved(kind);
        }

        return state;
    }

    private static void AddStatusTokens(List<string> tokens, string group, int? pi, long? rc, bool byp, string? scope, int? snap)
    {
        if (pi is { } units)
        {
            tokens.Add($"pi={group}:{units}");
        }

        if (rc is { } recycled)
        {
            tokens.Add($"rc={group}:{recycled}");
        }

        if (byp)
        {
            tokens.Add(NodeAdvert.Bypass);
        }

        if (scope is not null)
        {
            tokens.Add("ap=" + scope);
        }

        if (snap is { } seconds)
        {
            tokens.Add($"snap={seconds}");
        }
    }
}
