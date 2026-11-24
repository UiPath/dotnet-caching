namespace UiPath.Caching.Migration;

/// <summary>What the mover does with a key whose capture met a live supersession marker.</summary>
internal enum SupersessionAction
{
    /// <summary>Leave the key; it is counted <c>skipped{reason=superseded}</c> and retried on the next pass.</summary>
    Skip,

    /// <summary>Try the key once more in the same batch.</summary>
    Retry,

    /// <summary>The handler's control-checked delete of L answered that the identity changed: re-read the state and run the unit again under the new identity, without counting the key as helped.</summary>
    Rerun,
}
