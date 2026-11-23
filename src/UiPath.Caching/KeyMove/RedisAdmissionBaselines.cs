using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

// Remove a baseline by its token so delayed cleanup cannot remove a newer acquisition’s baseline.
internal sealed class RedisAdmissionBaselines(IDatabase dst, MoveKeyNames names, MoveScripts scripts) : IAdmissionBaselines
{
    private const string DstKind = "dst";
    private const string SrcKind = "src";
    private const CommandFlags Master = CommandFlags.DemandMaster;

    public Task<AdmissionBaseline?> ReadDstAsync(string app, CancellationToken cancellationToken) => ReadAsync(app, DstKind, cancellationToken);

    public Task<bool> WriteDstAsync(string app, AdmissionBaseline baseline, AdmissionBaseline? replaces, CancellationToken cancellationToken) => WriteAsync(app, DstKind, baseline, replaces, cancellationToken);

    public Task<AdmissionBaseline?> ReadSrcAsync(string app, CancellationToken cancellationToken) => ReadAsync(app, SrcKind, cancellationToken);

    public Task<bool> WriteSrcAsync(string app, AdmissionBaseline baseline, AdmissionBaseline? replaces, CancellationToken cancellationToken) => WriteAsync(app, SrcKind, baseline, replaces, cancellationToken);

    public async Task ClearAsync(string app, string token, CancellationToken cancellationToken)
    {
        foreach (var kind in new[] { DstKind, SrcKind })
        {
            var key = names.Baseline(app, kind);
            var text = await dst.StringGetAsync(key, Master).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (text.IsNull || AdmissionBaseline.Parse(text.ToString()).Token != token)
            {
                continue;
            }

            // Compare and delete in one transaction: a newer acquisition that replaced the value in between keeps it.
            var transaction = dst.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(key, text));
            _ = transaction.KeyDeleteAsync(key, Master);
            await transaction.ExecuteAsync(Master).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AdmissionBaseline?> ReadAsync(string app, string kind, CancellationToken cancellationToken)
    {
        var text = await dst.StringGetAsync(names.Baseline(app, kind), Master).WaitAsync(cancellationToken).ConfigureAwait(false);
        return text.IsNull ? null : AdmissionBaseline.Parse(text.ToString());
    }

    private async Task<bool> WriteAsync(string app, string kind, AdmissionBaseline baseline, AdmissionBaseline? replaces, CancellationToken cancellationToken) =>
        await scripts.SwapStringAsync(names.Baseline(app, kind), replaces?.Text ?? string.Empty, baseline.Text).WaitAsync(cancellationToken).ConfigureAwait(false);
}
