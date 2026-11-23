namespace UiPath.Caching.KeyMove;

/// <summary>Runs the Redis checks only after the checks against local evidence pass.</summary>
internal static class StartGates
{
    public static readonly string[] ReverseGates = [KeyMoveGateIds.StartKeyMoveSupport, KeyMoveGateIds.StartPins, KeyMoveGateIds.StartNodeList, KeyMoveGateIds.StartSnapshotFreshness];

    public static (List<GateFailure> Failures, List<string> Overrides) Cheap(StepInput i, StageDriverOptions options)
    {
        var failures = new List<GateFailure>();
        var overrides = new List<string>();
        var group = i.Group;
        AddNodeListFailures(i, failures);

        Overridable(i, ApprovalKinds.Start, KeyMoveGateIds.StartReleaseFloor, "release_floor_not_attested", i.Facts.ReleaseFloorAttested, failures, overrides, counted: false);
        Overridable(i, ApprovalKinds.StartUnsettled, KeyMoveGateIds.StartViewStable, "view_changed", i.ViewStable >= options.UpgradeHold, failures, overrides);
        if (Nodes(i, a => a.SnapshotSeconds is { } age && TimeSpan.FromSeconds(age) > i.StaleAfter) is { Length: > 0 } stale)
        {
            failures.Add(new GateFailure(KeyMoveGateIds.StartSnapshotFreshness, stale));
        }

        var dry = DryRunStamp.TryParse(i.State.Field(StateFields.DryRun));
        Overridable(i, ApprovalKinds.StartNoDryRun, KeyMoveGateIds.StartDryRun, "no_recent_dry_run", dry is not null && i.Now - dry.At <= options.DryRunMaxAge, failures, overrides);
        Overridable(i, ApprovalKinds.StartMemory, KeyMoveGateIds.StartMemoryHeadroom, "memory_plan", dry is not null && i.Facts.MemoryFits(group, dry.EstimatedBytes, options.MemoryHeadroom), failures, overrides);
        if (!i.Facts.FormatSettled(group))
        {
            failures.Add(new GateFailure(KeyMoveGateIds.StartFormatSettled, "format_not_settled"));
        }

        AddCacheRegistrationFailures(i, failures);

        return (failures, overrides);
    }

    /// <summary>Checks protocol support and configured replicas with the stored eviction consent, if any.</summary>
    public static async Task<GateFailure?> ServerAsync(StepInput i)
    {
        var preflight = await i.Preflight(Approval.ValidFor(i.State, ApprovalKinds.ControlRecordsEvictable) is not null).ConfigureAwait(false);
        return preflight.Ok ? null : new GateFailure(KeyMoveGateIds.StartServerPreflight, string.Join(',', preflight.Failures.Select(f => f.Code).Distinct()));
    }

    /// <summary>A start refuses existing owned native keys even with forced consent.</summary>
    public static async Task<GateFailure?> NativeEmptyAsync(StepInput i)
    {
        var found = await i.NativeCount().ConfigureAwait(false);
        return found == 0 ? null : new GateFailure(KeyMoveGateIds.StartNativeEmpty, $"native_keys:{found}");
    }

    public static async Task<GateFailure?> ReplicasAsync(StepInput i)
    {
        var preflight = await i.Preflight(true).ConfigureAwait(false);
        var replicas = preflight.Failures.Where(f => f.Code is "replica_unhealthy" or "replicas_unknown").Select(f => f.Code).Distinct().ToArray();
        return replicas.Length == 0 ? null : new GateFailure("preflight", string.Join(',', replicas));
    }

    public static (List<GateFailure> Failures, bool Forced) Reverse(StepInput i, StageDriverOptions options)
    {
        var failures = Cheap(i, options).Failures.Where(f => ReverseGates.Contains(f.Gate)).ToList();
        var forced = failures.Count > 0 && Approval.ValidFor(i.State, ApprovalKinds.ForceReverse) is not null;
        return (forced ? [] : failures, forced);
    }

    private static void AddNodeListFailures(StepInput i, List<GateFailure> failures)
    {
        var group = i.Group;
        if (i.Fresh.Count == 0)
        {
            failures.Add(new GateFailure(KeyMoveGateIds.StartNodeList, "no_fresh_advert"));
        }

        if (Nodes(i, a => !a.HasKm) is { Length: > 0 } noKm)
        {
            failures.Add(new GateFailure(KeyMoveGateIds.StartKeyMoveSupport, noKm));
        }

        if (Nodes(i, a => !a.Pinned(group) || a.Bypasses) is { Length: > 0 } unpinned)
        {
            failures.Add(new GateFailure(KeyMoveGateIds.StartPins, unpinned));
        }

        if (!i.Facts.NodeListComplete(group))
        {
            failures.Add(new GateFailure(KeyMoveGateIds.StartNodeList, "node_list_incomplete"));
        }

        if (Nodes(i, a => a.Scope != i.OwnScope) is { Length: > 0 } foreign)
        {
            failures.Add(new GateFailure(KeyMoveGateIds.StartForeignNode, $"foreign_node:{foreign}"));
        }
    }

    private static void AddCacheRegistrationFailures(StepInput i, List<GateFailure> failures)
    {
        if (Nodes(i, a => !a.Lists(i.Group)) is { Length: > 0 } unlisted)
        {
            failures.Add(new GateFailure(KeyMoveGateIds.StartGroupListed, unlisted));
        }

        var expected = i.State.CacheIds.Order(StringComparer.Ordinal).ToArray();
        if (Nodes(i, a => a.CachesOf(i.Group) is not { } ids || !ids.SequenceEqual(expected, StringComparer.Ordinal)) is { Length: > 0 } drift)
        {
            failures.Add(new GateFailure(KeyMoveGateIds.StartCacheSet, drift));
        }
    }

    private static string Nodes(StepInput i, Func<NodeAdvert, bool> predicate) => string.Join(',', i.Fresh.Where(predicate).Select(a => a.Node));

    private static void Overridable(StepInput i, string kind, string gate, string reason, bool holds, List<GateFailure> failures, List<string> overrides, bool counted = true)
    {
        if (holds)
        {
            return;
        }

        // Start consent supplies the release attestation, so it does not count as an override.
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
