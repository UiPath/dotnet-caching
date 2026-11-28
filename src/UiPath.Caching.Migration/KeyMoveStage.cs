namespace UiPath.Caching.Migration;

/// <summary>The stage of a key move group, as its stored state says.</summary>
public enum KeyMoveStage
{
    /// <summary>No move runs, and every cache reads and writes its in-place keys.</summary>
    NotStarted,

    /// <summary>Every node uses the move's write forms, the source remains authoritative, and keys have not started moving.</summary>
    Ready,

    /// <summary>During a forward move, caches without an origin stop writes while source writers drain; other caches continue as in <see cref="Ready"/>.</summary>
    Barrier,

    /// <summary>The destination is authoritative, reads promote source keys, and the mover copies the remaining keys.</summary>
    Moving,

    /// <summary>The mover found no remaining keys to move, and reads still check the source for counted residue.</summary>
    Drained,

    /// <summary>The forward move is final, the group uses native keys only, and leftover in-place keys are swept.</summary>
    Closed,
}
