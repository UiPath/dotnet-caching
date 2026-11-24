namespace UiPath.Caching.Migration;

/// <summary>One control-checked command of a hash write form; a form is a list of these.</summary>
internal enum HashWriteStep
{
    /// <summary>HSETC on L, with no fence.</summary>
    SrcWrite,

    /// <summary>SRCDEL on L.</summary>
    SrcDel,

    /// <summary>HDELC on L, with no fence.</summary>
    SrcFieldsDel,

    /// <summary>HSETC on N, replacing the fence.</summary>
    DstWrite,

    /// <summary>HSETC setm on N: the whole replace and the supersession marker of L in one script.</summary>
    DstWriteMarked,

    /// <summary>BUMP(G, N, del).</summary>
    BumpDel,

    /// <summary>HDELC on N, replacing the fence.</summary>
    DstFieldsDel,

    /// <summary>The native write of S5: HSETC on N with no fence.</summary>
    NativeWrite,

    /// <summary>The native <c>DEL N</c> of S5.</summary>
    NativeDel,

    /// <summary>The native field delete of S5: HDELC on N with no fence.</summary>
    NativeFieldsDel,
}
