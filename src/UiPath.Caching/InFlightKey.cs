namespace UiPath.Caching;

/// <summary>A composed key, the type read under it and the local lifetimes a hit is kept for, the parts of a policy a shared read uses: reads that differ in any of them are not shared.</summary>
internal readonly record struct InFlightKey(string Name, Type Type, TimeSpan? LocalExpiration, TimeSpan? LocalExpirationDisconnected);
