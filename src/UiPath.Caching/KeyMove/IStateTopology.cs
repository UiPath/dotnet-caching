using System.Net;

namespace UiPath.Caching.KeyMove;

/// <summary>Where the masters and slots are, and the dedicated connection to a master; a seam so a test can change the slot map between two readings.</summary>
internal interface IStateTopology
{
    Task<ClusterLayout> ReadLayoutAsync(CancellationToken cancellationToken);

    /// <summary>The replicas connected to the master now, and its eviction policy where the connection can read it.</summary>
    Task<MasterProbe> ProbeAsync(EndPoint master, CancellationToken cancellationToken);

    Task<IMasterChannel> OpenAsync(EndPoint master, CancellationToken cancellationToken);
}
