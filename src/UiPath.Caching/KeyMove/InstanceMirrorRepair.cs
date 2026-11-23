namespace UiPath.Caching.KeyMove;

internal sealed class InstanceMirrorRepair(ClaimStore dstMirror, Func<string, bool> holdsLease, Action<string, string, string>? alert = null)
{
    private const string MirrorKind = "imd:";

    public Task<ClaimOutcome> RestoreOpenAsync(string group, string token, CancellationToken cancellationToken) =>
        StepAsync(group, () => dstMirror.OpenAsync(MirrorKind + group, token, InstanceMoveClaim.Descriptor, restore: true), cancellationToken);

    public Task<ClaimOutcome> CertifyAsync(string group, string token, long ep, bool swept, CancellationToken cancellationToken) =>
        StepAsync(group, () => dstMirror.CertifyAsync(MirrorKind + group, token, ep, swept), cancellationToken);

    public Task<ClaimOutcome> IdleAsync(string group, string token, CancellationToken cancellationToken) =>
        StepAsync(group, () => dstMirror.IdleAsync(MirrorKind + group, token), cancellationToken);

    public Task<ClaimOutcome> CloseAsync(string group, string token, CancellationToken cancellationToken) =>
        StepAsync(group, () => dstMirror.CloseAsync(MirrorKind + group, token), cancellationToken);

    private async Task<ClaimOutcome> StepAsync(string group, Func<Task<ClaimResult>> step, CancellationToken cancellationToken)
    {
        if (!holdsLease(group))
        {
            alert?.Invoke(group, "lease_lost", "the mirror repair was not sent");
            return ClaimOutcome.Failed;
        }

        try
        {
            var result = await step().ConfigureAwait(false);
            var outcome = InstanceMoveClaim.Map(result);
            if (result.Code is ClaimCodes.OtherGroup or ClaimCodes.OtherMove)
            {
                alert?.Invoke(group, "claim_conflict", $"the Dst claim record refused the mirror repair ({result.Code})");
            }
            else if (outcome is ClaimOutcome.DoneUnconfirmed or ClaimOutcome.Failed)
            {
                alert?.Invoke(group, "wait_skipped", "claim,dst");
            }

            return outcome;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            alert?.Invoke(group, "wait_skipped", "claim,dst");
            return ClaimOutcome.Failed;
        }
    }
}
