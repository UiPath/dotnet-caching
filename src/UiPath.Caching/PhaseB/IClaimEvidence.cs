namespace UiPath.Caching.PhaseB;

/// <summary>What the group's control records and state show about its end, for the operator commands; the state store implements it.</summary>
internal interface IClaimEvidence
{
    /// <summary>A committed record at an epoch that shows S0, or S5 with the state's <c>lswept</c> equal to that record's id; null when there is none.</summary>
    Task<ClaimTerminal?> ReadTerminalAsync(string group, CancellationToken cancellationToken);

    /// <summary>Whether any record of the group (a sample of every master) or its state shows S0 or S5, with or without <c>lswept</c>.</summary>
    Task<bool> ShowsTerminalAsync(string group, CancellationToken cancellationToken);
}
