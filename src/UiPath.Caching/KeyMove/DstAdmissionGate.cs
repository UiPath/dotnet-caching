namespace UiPath.Caching.KeyMove;

// A replica that lost both the baseline and the mirror is indistinguishable from one with no move.
internal sealed class DstAdmissionGate(IGenerationSource generations, IAdmissionBaselines baselines, Action<string, string, string>? alert = null)
{
    public async Task<AdmissionDecision> CheckAsync(string app, CancellationToken cancellationToken)
    {
        AdmissionBaseline? baseline;
        try
        {
            baseline = await baselines.ReadDstAsync(app, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException or FormatException)
        {
            return Refused(app, $"the Dst generation baseline cannot be read: {ex.GetType().Name}");
        }

        if (baseline is null)
        {
            return AdmissionDecision.Passed;
        }

        var current = await generations.ReadAsync(RecordSide.Dst, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Refused(app, "the Dst generation cannot be read");
        }

        return current == baseline.Generation ? AdmissionDecision.Passed : Refused(app, "the Dst generation changed since the mirror was confirmed; the mirror is not repaired");
    }

    private AdmissionDecision Refused(string app, string detail)
    {
        alert?.Invoke(app, KeyMoveGateIds.MirrorRepairPending, detail);
        return AdmissionDecision.Refuse(KeyMoveGateIds.MirrorRepairPending, detail);
    }
}
