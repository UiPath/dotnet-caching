namespace UiPath.Caching.Migration;

/// <summary>The stage of a key move group, as its stored state says.</summary>
public enum KeyMoveStage
{
    /// <summary>No move runs: every cache of the group reads and writes its in-place keys as before (state S0).</summary>
    NotStarted,

    /// <summary>Every node has switched to the move's write forms, with the source side still the authority, and no key moves yet (state S1).</summary>
    Ready,

    /// <summary>The writes of the caches that have no origin are closed while the last writers of the source side drain; the other caches go on as in <see cref="Ready"/> (state S2, forward only).</summary>
    Barrier,

    /// <summary>The destination side is the authority: reads promote keys from the source side, and the mover copies the rest (state S3).</summary>
    Moving,

    /// <summary>The mover found no key left to move; reads still fall back to the source side for the residue the census counted (state S4).</summary>
    Drained,

    /// <summary>The move is final: the group reads and writes the native keys only, and the leftover in-place keys are swept (state S5, forward only).</summary>
    Closed,
}
