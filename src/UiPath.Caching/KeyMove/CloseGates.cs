namespace UiPath.Caching.KeyMove;

internal static class CloseGates
{
    /// <summary>The configuration attests that the in-place readers are still deployed, the consent acknowledges the residue, and the inventory runs only after these checks pass.</summary>
    public static async Task<CloseGateResult> CheapAsync(StepInput i, StageDriverOptions options)
    {
        var state = i.State;
        var failures = new List<GateFailure>();
        var overrides = new List<string>();
        var forced = Approval.ValidFor(state, ApprovalKinds.CloseForce) is not null;
        if (i.Fresh.Count == 0 || i.Fresh.Any(a => !a.Acknowledges(i.Group, state)))
        {
            failures.Add(new GateFailure(KeyMoveGateIds.CloseAcks, string.Join(',', i.Fresh.Where(a => !a.Acknowledges(i.Group, state)).Select(a => a.Node))));
        }

        var soak = i.Facts.LongestSourceTtl(i.Group) is { } ttl && ttl < options.DrainedSoak ? ttl : options.DrainedSoak;
        var drainedAt = Moment(state.Field(StateFields.DrainedAt));
        if (drainedAt is null || i.Now - drainedAt < soak)
        {
            Overridable(KeyMoveGateIds.CloseSoak, drainedAt is null ? "drained_at_unknown" : "soak", forced, failures, overrides);
        }

        // The soak restarts when a pass counts a post-transition write. An absent mover report adds no evidence.
        var canaryAt = Moment(state.Field(StateFields.CanaryAt));
        var drain = await i.Drain().ConfigureAwait(false);
        if ((drain is { Canary: > 0 }) || (canaryAt is not null && i.Now - canaryAt < soak))
        {
            Overridable(KeyMoveGateIds.CloseCanary, "canary", forced, failures, overrides);
        }

        failures.AddRange(Acknowledged(i, state));
        if (i.Fresh.Where(a => a.Bypasses || !a.HasKm).Select(a => a.Node).ToArray() is { Length: > 0 } bad)
        {
            failures.Add(new GateFailure(KeyMoveGateIds.CloseNodeReadiness, string.Join(',', bad)));
        }

        // No override: Close is the point of no return, so the code that could take the group back must still be there.
        if (!i.Facts.InPlaceReadersDeployed(i.Group))
        {
            failures.Add(new GateFailure(KeyMoveGateIds.CloseInPlaceReaders, "in_place_readers_not_attested"));
        }

        if (i.Flag.Confirmed < options.CloseConfirm)
        {
            failures.Add(new GateFailure(KeyMoveGateIds.CloseViews, "views_not_confirmed"));
        }

        return new CloseGateResult(failures, overrides);
    }

    /// <summary>Checks current residue on the masters against the approved counts.</summary>
    public static async Task<GateFailure?> InventoryAsync(StepInput i)
    {
        var inventory = await i.ResidueInventory().ConfigureAwait(false);
        var grown = i.State.CacheIds.Where(c => inventory.GetValueOrDefault(c) > (i.State.Residue is { } r && r.TryGetValue(c, out var n) ? n : 0)).ToArray();
        return grown.Length == 0 ? null : new GateFailure(KeyMoveGateIds.CloseInventory, string.Join(',', grown));
    }

    // Consent covers the stored counts. A cache with residue and no origin needs explicit refill support.
    private static IEnumerable<GateFailure> Acknowledged(StepInput i, StateRecord state)
    {
        if (Approval.ValidFor(state, ApprovalKinds.Close) is null && Approval.ValidFor(state, ApprovalKinds.CloseForce) is null)
        {
            yield return new GateFailure(KeyMoveGateIds.CloseConsent, "no_consent");
            yield break;
        }

        var unknown = state.CacheIds.Where(c => state.Residue is null || !state.Residue.ContainsKey(c)).ToArray();
        if (unknown.Length > 0)
        {
            yield return new GateFailure(KeyMoveGateIds.CloseConsent, $"residue_unknown:{string.Join(',', unknown)}");
        }

        foreach (var cache in state.CacheIds)
        {
            if (state.Residue is { } residue && residue.GetValueOrDefault(cache) > 0 && i.Facts.HasNoOrigin(i.Group, [cache]) && !i.Facts.AllAcceptRefill(i.Group, [cache]))
            {
                yield return new GateFailure(KeyMoveGateIds.CloseConsent, $"noorigin_residue:{cache}");
            }
        }
    }

    private static void Overridable(string gate, string reason, bool forced, List<GateFailure> failures, List<string> overrides)
    {
        if (forced)
        {
            overrides.Add(gate);
        }
        else
        {
            failures.Add(new GateFailure(gate, reason));
        }
    }

    private static DateTimeOffset? Moment(string text) => long.TryParse(text, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}
