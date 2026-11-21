namespace UiPath.Caching.PhaseB;

/// <summary>The gates G1 to G14 of 2.4. The cheap ones read the adverts and the state; G12 and G14 read Redis and run only when the others hold.</summary>
internal static class StartGates
{
    public static readonly string[] ReverseGates = ["G1", "G2", "G3", "G7"];

    /// <summary>G1, G2, G3, G4, G5, G6, G7, G8, G9, G10, G11, G13 from what is already in memory, and the overrides that were used.</summary>
    public static (List<GateFailure> Failures, List<string> Overrides) Cheap(StepInput i, StageDriverOptions options)
    {
        var failures = new List<GateFailure>();
        var overrides = new List<string>();
        var group = i.Group;
        void Fail(string gate, string reason) => failures.Add(new GateFailure(gate, reason));
        string Nodes(Func<NodeAdvert, bool> bad) => string.Join(',', i.Fresh.Where(bad).Select(a => a.Node));

        if (i.Fresh.Count == 0)
        {
            Fail("G3", "no_fresh_advert");
        }

        if (Nodes(a => !a.HasPb) is { Length: > 0 } noPb)
        {
            Fail("G1", noPb);
        }

        if (Nodes(a => !a.Pinned(group) || a.Bypasses) is { Length: > 0 } unpinned)
        {
            Fail("G2", unpinned);
        }

        if (!i.Facts.NodeListComplete(group))
        {
            Fail("G3", "node_list_incomplete");
        }

        if (Nodes(a => a.Scope != i.OwnScope) is { Length: > 0 } foreign)
        {
            Fail("G4", $"foreign_node:{foreign}");
        }

        Overridable(i, ApprovalKinds.Start, "G5", "release_floor_not_attested", i.Facts.ReleaseFloorAttested, failures, overrides, counted: false);
        Overridable(i, ApprovalKinds.StartUnsettled, "G6", "view_changed", i.ViewStable >= options.UpgradeHold, failures, overrides);
        if (Nodes(a => a.SnapshotSeconds is { } age && TimeSpan.FromSeconds(age) > i.StaleAfter) is { Length: > 0 } stale)
        {
            Fail("G7", stale);
        }

        var dry = DryRunStamp.TryParse(i.State.Field(StateFields.DryRun));
        Overridable(i, ApprovalKinds.StartNoDryRun, "G8", "no_recent_dry_run", dry is not null && i.Now - dry.At <= options.DryRunMaxAge, failures, overrides);
        Overridable(i, ApprovalKinds.StartMemory, "G9", "memory_plan", dry is not null && i.Facts.MemoryFits(group, dry.EstimatedBytes, options.MemoryHeadroom), failures, overrides);
        if (!i.Facts.FormatSettled(group))
        {
            Fail("G10", "format_not_settled");
        }

        if (Nodes(a => !a.Lists(group)) is { Length: > 0 } unlisted)
        {
            Fail("G11", unlisted);
        }

        var expected = i.State.CacheIds.Order(StringComparer.Ordinal).ToArray();
        if (Nodes(a => a.CachesOf(group) is not { } ids || !ids.SequenceEqual(expected, StringComparer.Ordinal)) is { Length: > 0 } drift)
        {
            Fail("G13", drift);
        }

        return (failures, overrides);
    }

    /// <summary>G12 for X1: the server supports the protocol and the replicas are as configured, or the eviction override is stored.</summary>
    public static async Task<GateFailure?> ServerAsync(StepInput i)
    {
        var preflight = await i.Preflight(Approval.ValidFor(i.State, ApprovalKinds.ControlRecordsEvictable) is not null).ConfigureAwait(false);
        return preflight.Ok ? null : new GateFailure("G12", string.Join(',', preflight.Failures.Select(f => f.Code).Distinct()));
    }

    /// <summary>G14: no owned N key; no override.</summary>
    public static async Task<GateFailure?> NativeEmptyAsync(StepInput i)
    {
        var found = await i.NativeCount().ConfigureAwait(false);
        return found == 0 ? null : new GateFailure("G14", $"native_keys:{found}");
    }

    /// <summary>Step 0 of a forward transition other than X1: the replicas are as configured.</summary>
    public static async Task<GateFailure?> ReplicasAsync(StepInput i)
    {
        var preflight = await i.Preflight(true).ConfigureAwait(false);
        var replicas = preflight.Failures.Where(f => f.Code is "replica_unhealthy" or "replicas_unknown").Select(f => f.Code).Distinct().ToArray();
        return replicas.Length == 0 ? null : new GateFailure("preflight", string.Join(',', replicas));
    }

    /// <summary>The gates of X7: G1, G2, G3, G7, or a stored forced reverse, which is counted as an override.</summary>
    public static (List<GateFailure> Failures, bool Forced) Reverse(StepInput i, StageDriverOptions options)
    {
        var failures = Cheap(i, options).Failures.Where(f => ReverseGates.Contains(f.Gate)).ToList();
        var forced = failures.Count > 0 && Approval.ValidFor(i.State, ApprovalKinds.ForceReverse) is not null;
        return (forced ? [] : failures, forced);
    }

    private static void Overridable(StepInput i, string kind, string gate, string reason, bool holds, List<GateFailure> failures, List<string> overrides, bool counted = true)
    {
        if (holds)
        {
            return;
        }

        // G5 is attested by the stored start consent itself, so it is no override; the others are logged and counted.
        if (Approval.ValidFor(i.State, kind) is not null)
        {
            if (counted)
            {
                overrides.Add(kind);
            }

            return;
        }

        failures.Add(new GateFailure(gate, reason));
    }
}
