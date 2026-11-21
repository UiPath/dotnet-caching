using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>Which master owns each slot, and how many replicas each master is configured with, at one reading of the topology.</summary>
internal sealed class ClusterLayout
{
    private readonly EndPoint?[] _owners = new EndPoint?[HashSlot.Count];

    public ClusterLayout(IReadOnlyList<MasterLayout> masters)
    {
        Masters = masters;
        foreach (var master in masters)
        {
            foreach (var range in master.Slots)
            {
                for (var slot = range.From; slot <= range.To; slot++)
                {
                    _owners[slot] = master.Endpoint;
                }
            }
        }
    }

    public IReadOnlyList<MasterLayout> Masters { get; }

    public EndPoint OwnerOf(int slot) =>
        _owners[slot] ?? throw new RedisException($"No master owns slot {slot}.");

    public MasterLayout MasterAt(EndPoint endpoint) =>
        Masters.First(m => m.Endpoint.Equals(endpoint));

    /// <summary>The slots among <paramref name="slots"/> whose owner differs from the one in <paramref name="other"/>.</summary>
    public IReadOnlyList<int> MovedSince(ClusterLayout other, IEnumerable<int> slots) =>
        [.. slots.Where(s => !OwnerOf(s).Equals(other.OwnerOf(s)))];
}
