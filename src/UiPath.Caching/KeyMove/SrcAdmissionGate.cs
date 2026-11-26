namespace UiPath.Caching.KeyMove;

// After a Src generation change, a settled mirror must carry a not-started certificate for the baseline token.
internal sealed class SrcAdmissionGate(Func<bool> registered, IGenerationSource generations, IAdmissionBaselines baselines, Func<CancellationToken, Task<ClaimRecord>> readMirror, Action<string, string, string>? alert = null)
{
    private const string MirrorKind = "imd:";

    public async Task<AdmissionDecision> CheckAsync(string app, CancellationToken cancellationToken)
    {
        if (!registered())
        {
            return AdmissionDecision.Passed;
        }

        AdmissionBaseline? baseline;
        try
        {
            baseline = await baselines.ReadSrcAsync(app, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException or FormatException)
        {
            return Refused(app, $"the Src generation baseline cannot be read: {ex.GetType().Name}");
        }

        if (baseline is null)
        {
            return await EvidenceAsync(app, null, cancellationToken).ConfigureAwait(false);
        }

        var current = await generations.ReadAsync(RecordSide.Src, cancellationToken).ConfigureAwait(false);
        if (current == baseline.Generation)
        {
            return AdmissionDecision.Passed;
        }

        return await EvidenceAsync(app, baseline, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdmissionDecision> EvidenceAsync(string app, AdmissionBaseline? baseline, CancellationToken cancellationToken)
    {
        var before = await generations.ReadPairAsync(cancellationToken).ConfigureAwait(false);
        ClaimRecord mirror;
        try
        {
            mirror = await readMirror(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException)
        {
            return Refused(app, $"the Dst mirror cannot be read: {ex.GetType().Name}");
        }

        var after = await generations.ReadPairAsync(cancellationToken).ConfigureAwait(false);
        if (before is null || after is null || before != after)
        {
            return Refused(app, "a generation is unknown or changed while the Dst mirror was read");
        }

        if (mirror.Malformed)
        {
            return Refused(app, "the Dst mirror is not recognized");
        }

        var fields = mirror.Moves.Where(m => m.Key.StartsWith(MirrorKind, StringComparison.Ordinal)).Select(static m => m.Value).ToArray();
        if (fields.Any(static f => f.IsLive))
        {
            return Refused(app, "the Dst mirror shows a move");
        }

        if (baseline is null)
        {
            return AdmissionDecision.Passed;
        }

        return fields.Any(f => f is { Status: ClaimGrammar.Idle, Cert: { } cert } && f.Token == baseline.Token && cert.EndsWith(":" + KeyMoveStates.NotStarted, StringComparison.Ordinal))
            ? AdmissionDecision.Passed
            : Refused(app, "the Dst mirror shows no fresh certificate that the move ended");
    }

    private AdmissionDecision Refused(string app, string detail)
    {
        alert?.Invoke(app, KeyMoveGateIds.SrcAdmissionPending, detail);
        return AdmissionDecision.Refuse(KeyMoveGateIds.SrcAdmissionPending, detail);
    }
}
