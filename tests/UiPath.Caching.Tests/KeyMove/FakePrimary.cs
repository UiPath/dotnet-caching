using System.Net;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>One primary of a <see cref="FakeSide"/>: its replicas and what its server answers or throws.</summary>
/// <param name="EndPoint">The primary's endpoint.</param>
/// <param name="Connected">False lists the primary without a connection.</param>
/// <param name="Replicas">The replicas ROLE lists.</param>
/// <param name="Role">Thrown by ROLE when set.</param>
/// <param name="Wait">The WAIT reply: a number, or an exception to throw; 1 when null.</param>
/// <param name="Config">Thrown by CONFIG GET when set.</param>
/// <param name="Info">Thrown by INFO when set.</param>
/// <param name="Scan">Thrown by SCAN when set.</param>
/// <param name="ScanEndless">SCAN never returns cursor 0, so a traversal only ends when its budget does.</param>
/// <param name="OnScan">Runs on every SCAN step, so a test can move a clock.</param>
/// <param name="Client">Thrown by CLIENT ID when set.</param>
internal sealed record FakePrimary(
    EndPoint EndPoint,
    bool Connected = true,
    int Replicas = 0,
    Exception? Role = null,
    object? Wait = null,
    Exception? Config = null,
    Exception? Info = null,
    Exception? Scan = null,
    bool ScanEndless = false,
    Action? OnScan = null,
    Exception? Client = null);
