using System.Net;

namespace UiPath.Caching.PhaseB;

/// <summary>Where the masters and slots are, and the dedicated connection to a master; a seam so a test can change the slot map between two readings.</summary>
internal interface IStateTopology
{
    Task<ClusterLayout> ReadLayoutAsync(CancellationToken cancellationToken);

    Task<IMasterChannel> OpenAsync(EndPoint master, CancellationToken cancellationToken);
}
