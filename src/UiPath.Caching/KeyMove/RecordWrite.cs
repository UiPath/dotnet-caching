namespace UiPath.Caching.KeyMove;

/// <summary>After a retry, the code describes the last attempt. CTLSYNC callers verify repeats by reading back.</summary>
internal readonly record struct RecordWrite(BarrierOutcome Outcome, long Code, Exception? Uncertain, int Attempts = 1, ConnectionProof? Proof = null)
{
    public const long NoCode = long.MinValue;

    public bool Confirmed => Outcome is BarrierOutcome.Confirmed or BarrierOutcome.NoReplica;
}
