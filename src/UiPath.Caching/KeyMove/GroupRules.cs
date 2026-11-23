namespace UiPath.Caching.KeyMove;

/// <summary>What a node does for one group right now, from its snapshot, its last known state and the clock.</summary>
/// <param name="WriteWait">For <see cref="WriteRule.WaitThenFailClosed"/>, how much of <c>TransitionWait</c> is left.</param>
/// <param name="WithdrawAcks">The node advertises no identity (<c>&lt;g&gt;@0</c>) for the group.</param>
/// <param name="Ready">False for a group still unknown after <c>StartupWait</c>; affects readiness reporting only.</param>
internal sealed record GroupRules(
    bool Relevant,
    ReadRule Reads,
    WriteRule Writes,
    ActedState? Acting,
    TimeSpan? WriteWait,
    bool WithdrawAcks,
    bool Ready,
    IReadOnlyList<string> Alerts)
{
    public static GroupRules Unaffected { get; } = new(false, ReadRule.Follow, WriteRule.Follow, null, null, false, true, []);
}
