namespace UiPath.Caching.KeyMove;

/// <param name="MaxmemoryPolicy">Null when the connection may not read it.</param>
internal sealed record MasterProbe(int ConnectedReplicas, string? MaxmemoryPolicy);
