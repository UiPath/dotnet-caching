namespace UiPath.Caching.PhaseB;

/// <summary>The operator's claim commands: <c>ReleaseClaim</c>, <c>CertifyClaim</c> and <c>RemoveGroup</c>.</summary>
internal sealed class ClaimOperator(ClaimStore store, IClaimEvidence evidence)
{
    /// <summary>Deletes a field that still holds <paramref name="expected"/>; an open field whose group shows a terminal state needs <see cref="CertifyAsync"/> instead.</summary>
    /// <remarks>
    /// A <c>prv:</c> pointer is refused while its group has a move field, whose later idle would restore it: only <see cref="RemoveGroupAsync"/> drops it then.
    /// The evidence and record reads are not atomic with the drop, which is fenced by the field's value alone.
    /// </remarks>
    public async Task<ClaimCommandOutcome> ReleaseAsync(string field, string expected, string confirm, CancellationToken cancellationToken)
    {
        if (ClaimGrammar.ValidField(field) is { } pointer && pointer.Kind == "prv")
        {
            var record = await store.ReadAsync().ConfigureAwait(false);
            if (record.Raw.Any(e => ClaimGrammar.ValidField(e.Key) is { } other && other.Group == pointer.Group && ClaimGrammar.IsMove(other.Kind)))
            {
                return ClaimCommandOutcome.Refused;
            }
        }

        if (ClaimGrammar.ValidField(field) is { } parsed && ClaimGrammar.IsMove(parsed.Kind)
            && ClaimGrammar.Parse(expected) is { IsMalformed: false, Status: ClaimGrammar.Open }
            && await evidence.ShowsTerminalAsync(parsed.Group, cancellationToken).ConfigureAwait(false))
        {
            return ClaimCommandOutcome.NeedsCertify;
        }

        return ToOutcome(await store.DropAsync(field, expected, confirm).ConfigureAwait(false));
    }

    /// <summary>For an open field with no cert, still holding <paramref name="expected"/> as shown: writes the group's terminal evidence as its cert, then idles (S0) or closes (S5) it.</summary>
    /// <returns><see cref="ClaimCommandOutcome.DoneUnconfirmed"/> when a WAIT did not confirm a step: the caller raises <c>wait_skipped{claim}</c>.</returns>
    public async Task<ClaimCommandOutcome> CertifyAsync(string field, string expected, string confirm, CancellationToken cancellationToken)
    {
        if (confirm.Length == 0 || ClaimGrammar.ValidField(field) is not { } parsed || !ClaimGrammar.IsMove(parsed.Kind))
        {
            return ClaimCommandOutcome.Refused;
        }

        var record = await store.ReadAsync().ConfigureAwait(false);
        if (record.ValueOf(field) != expected)
        {
            return ClaimCommandOutcome.Changed;
        }

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
        if (end.Code is not (ClaimCodes.Done or ClaimCodes.AlreadyDone))
        {
            return ToOutcome(end.Code);
        }

        return cert.Durability == ClaimDurability.NotConfirmed || end.Durability == ClaimDurability.NotConfirmed ? ClaimCommandOutcome.DoneUnconfirmed : ClaimCommandOutcome.Done;
    }

    /// <summary>Drops the group's move fields, then its <c>prv:</c> pointer, each by its current value; <c>cur:fm</c> stays, so admission keeps protecting the final format.</summary>
    /// <remarks>
    /// Refused while a field of the group is live or the group is not removable (SO14 and <c>lswept</c>); a field that changed stops it before the pointer is touched.
    /// The removable check is not atomic with the drops; each drop is fenced by the value read.
    /// </remarks>
    public async Task<ClaimCommandOutcome> RemoveGroupAsync(string group, string confirm, CancellationToken cancellationToken)
    {
        var record = await store.ReadAsync().ConfigureAwait(false);
        var moves = new List<KeyValuePair<string, string>>();
        string? previous = null;
        foreach (var (field, value) in record.Raw)
        {
            if (ClaimGrammar.ValidField(field) is not { } parsed || parsed.Group != group)
            {
                continue;
            }

            if (parsed.Kind == "prv")
            {
                previous = value;
            }
            else if (ClaimGrammar.IsMove(parsed.Kind))
            {
                if (ClaimGrammar.Parse(value) is { IsLive: true })
                {
                    return ClaimCommandOutcome.Live;
                }

                moves.Add(new(field, value));
            }
        }

        if (!await evidence.RemovableAsync(group, cancellationToken).ConfigureAwait(false))
        {
            return ClaimCommandOutcome.NotRemovable;
        }

        foreach (var (field, value) in moves)
        {
            var code = await store.DropAsync(field, value, confirm).ConfigureAwait(false);
            if (code is not (ClaimCodes.Done or ClaimCodes.AlreadyDone))
            {
                return ToOutcome(code);
            }
        }

        return previous is null ? ClaimCommandOutcome.Done : ToOutcome(await store.DropAsync("prv:" + group, previous, confirm).ConfigureAwait(false));
    }

    private static ClaimCommandOutcome ToOutcome(long code) => code switch
    {
        ClaimCodes.Done or ClaimCodes.AlreadyDone => ClaimCommandOutcome.Done,
        ClaimCodes.Refused => ClaimCommandOutcome.Changed,
        ClaimCodes.FallbackExhausted => ClaimCommandOutcome.Unknown,
        _ => ClaimCommandOutcome.Refused,
    };
}
