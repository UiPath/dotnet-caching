namespace UiPath.Caching.KeyMove;

internal enum PreStep
{
    None,

    /// <summary>The owned-N inventory, a purge of any key it finds, and the inventory again (X3).</summary>
    NativeInventory,

    /// <summary>Sweep N before the flip to off (X10).</summary>
    SweepNative,
}
