namespace UiPath.Caching.KeyMove;

/// <summary>What a transition does to the forward generation <c>fg</c> and the retiring one <c>rg</c> over a run.</summary>
internal enum BindingChange
{
    None,

    /// <summary>The start: the new identity's id becomes the forward generation.</summary>
    Mint,

    /// <summary>The reverse begins: the forward generation retires; it moves to <c>rg</c> and <c>fg</c> is cleared.</summary>
    Retire,

    /// <summary>The retire gate was passed; <c>rg</c> is cleared.</summary>
    ClearRetiring,

    /// <summary>The group is back in off and holds neither.</summary>
    ClearAll,
}
