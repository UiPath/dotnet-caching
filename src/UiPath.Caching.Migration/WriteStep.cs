namespace UiPath.Caching.Migration;

/// <summary>One control-checked command in the ordered steps of a write form.</summary>
internal enum WriteStep
{
    /// <summary>SRCSET on L.</summary>
    SrcSet,

    /// <summary>SRCDEL on L.</summary>
    SrcDel,

    /// <summary>BUMP(G, N, set).</summary>
    BumpSet,

    /// <summary>BUMP(G, N, setm): the set and the supersession marker of L in one script.</summary>
    BumpSetMarked,

    /// <summary>BUMP(G, N, del).</summary>
    BumpDel,

    /// <summary>The native <c>SET N</c> of S5.</summary>
    NativeSet,

    /// <summary>The native <c>DEL N</c> of S5.</summary>
    NativeDel,
}
