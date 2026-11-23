namespace UiPath.Caching.KeyMove;

/// <param name="Missing">Some fresh node advertises no <c>fl</c>: it blocks moving forward and never starts a way back.</param>
/// <param name="Min">The lowest view among the fresh nodes that have one; null when none has.</param>
/// <param name="Lowered">The lowest view that persisted for <c>FlagLowerHold</c> while some node stayed below the current stage; null when no way back is asked.</param>
/// <param name="Confirmed">How long every fresh view has been at or above the confirmed level, for <c>CloseConfirm</c>.</param>
internal sealed record FlagVerdict(int Fresh, bool Missing, int? Min, int? Lowered, TimeSpan Confirmed)
{
    public static FlagVerdict None { get; } = new(0, false, null, null, TimeSpan.Zero);

    public bool AllAtLeast(int stage) => Fresh > 0 && !Missing && Min >= stage;
}
