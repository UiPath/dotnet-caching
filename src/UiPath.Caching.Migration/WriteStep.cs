namespace UiPath.Caching.Migration;

/// <summary>One control-checked command in the ordered steps of a write form.</summary>
internal enum WriteStep
{
    /// <summary>SRCSET on Src: L forward, N in reverse.</summary>
    SrcSet,

    /// <summary>SRCDEL on Src: L forward, N in reverse.</summary>
    SrcDel,

    /// <summary>BUMP(G, N, set) forward, BUMP(GL, L, set) in reverse.</summary>
    BumpSet,

    /// <summary>BUMP(G, N, setm): the set and the supersession marker of L in one script.</summary>
    BumpSetMarked,

    /// <summary>BUMP(G, N, del) forward, BUMP(GL, L, del) in reverse.</summary>
    BumpDel,

    /// <summary>The native <c>SET N</c> of closed.</summary>
    NativeSet,

    /// <summary>The native <c>DEL N</c> of closed.</summary>
    NativeDel,
}
