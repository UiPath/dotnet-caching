namespace UiPath.Caching.PhaseB;

/// <summary>The operator's claim commands: <c>ReleaseClaim</c>, <c>CertifyClaim</c> and <c>RemoveGroup</c>.</summary>
internal sealed class ClaimOperator(ClaimStore store, IClaimEvidence evidence)
{
    /// <summary>
    /// Deletes a field that still holds <paramref name="expected"/>, the value shown to the operator. An open field whose group shows a terminal state is
    /// refused: it needs <see cref="CertifyAsync"/>, or a plain pod would be admitted on the old format.
    /// </summary>
    public async Task<ClaimCommandOutcome> ReleaseAsync(string field, string expected, string confirm, CancellationToken cancellationToken)
    {
        if (ClaimGrammar.ValidField(field) is { } parsed && ClaimGrammar.IsMove(parsed.Kind)
            && ClaimGrammar.Parse(expected) is { IsMalformed: false, Status: ClaimGrammar.Open }
            && await evidence.ShowsTerminalAsync(parsed.Group, cancellationToken).ConfigureAwait(false))
        {
            return ClaimCommandOutcome.NeedsCertify;
        }

        return ToOutcome(await store.DropAsync(field, expected, confirm).ConfigureAwait(false));
    }

    /// <summary>For an open field with no cert: takes the group's terminal evidence, writes the cert, then idles (S0) or closes (S5) the field, each confirmed by WAIT.</summary>
    public async Task<ClaimCommandOutcome> CertifyAsync(string field, CancellationToken cancellationToken)
    {
        if (ClaimGrammar.ValidField(field) is not { } parsed || !ClaimGrammar.IsMove(parsed.Kind))
        {
            return ClaimCommandOutcome.Refused;
        }

        var record = await store.ReadAsync().ConfigureAwait(false);
        if (!record.Moves.TryGetValue(field, out var value) || value.Status != ClaimGrammar.Open || value.Cert.Length != 0)
        {
            return ClaimCommandOutcome.Refused;
        }

        if (await evidence.ReadTerminalAsync(parsed.Group, cancellationToken).ConfigureAwait(false) is not { } terminal)
        {
            return ClaimCommandOutcome.NoEvidence;
        }

        var cert = await store.CertifyAsync(field, value.Token, terminal.Epoch, terminal.Swept).ConfigureAwait(false);
        if (cert.Code is not (ClaimCodes.Done or ClaimCodes.AlreadyDone))
        {
            return ToOutcome(cert.Code);
        }

        var end = terminal.Swept ? await store.CloseAsync(field, value.Token).ConfigureAwait(false) : await store.IdleAsync(field, value.Token).ConfigureAwait(false);
        return ToOutcome(end.Code);
    }

    /// <summary>Drops the group's move fields and its <c>prv:</c> pointer, each by its current value; <c>cur:fm</c> stays, so admission keeps protecting the final format.</summary>
    public async Task<ClaimCommandOutcome> RemoveGroupAsync(string group, string confirm)
    {
        var record = await store.ReadAsync().ConfigureAwait(false);
        var outcome = ClaimCommandOutcome.Done;
        foreach (var (field, value) in record.Raw)
        {
            if (ClaimGrammar.ValidField(field) is { } parsed && parsed.Group == group && parsed.Kind != "cur")
            {
                var code = await store.DropAsync(field, value, confirm).ConfigureAwait(false);
                if (code is not (ClaimCodes.Done or ClaimCodes.AlreadyDone))
                {
                    outcome = ToOutcome(code);
                }
            }
        }

        return outcome;
    }

    private static ClaimCommandOutcome ToOutcome(long code) => code switch
    {
        ClaimCodes.Done or ClaimCodes.AlreadyDone => ClaimCommandOutcome.Done,
        ClaimCodes.Refused => ClaimCommandOutcome.Changed,
        ClaimCodes.FallbackExhausted => ClaimCommandOutcome.Unknown,
        _ => ClaimCommandOutcome.Refused,
    };
}
