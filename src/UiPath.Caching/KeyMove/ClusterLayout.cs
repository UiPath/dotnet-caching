using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class ClusterLayout
{
    private readonly EndPoint?[] _owners = new EndPoint?[HashSlot.Count];

    public ClusterLayout(IReadOnlyList<MasterLayout> masters, IReadOnlySet<int>? unsettled = null)
    {
        Masters = masters;
        Unsettled = unsettled ?? new HashSet<int>();
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

    /// <summary>Slots that are migrating or importing: a write there may land on the node the owner is handing the slot to, which no WAIT on the owner covers.</summary>
    public IReadOnlySet<int> Unsettled { get; }

    /// <summary>The slots a CLUSTER NODES line shows in transit: <c>[93-&gt;-id]</c> migrating, <c>[77-&lt;-id]</c> importing.</summary>
    public static IEnumerable<int> ParseTransitions(string nodeLine) =>
        TransitionPattern().Matches(nodeLine).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

    public EndPoint OwnerOf(int slot) =>
        _owners[slot] ?? throw new RedisException($"No master owns slot {slot}.");

    public MasterLayout MasterAt(EndPoint endpoint) =>
        Masters.First(m => EndPointMatch.Same(m.Endpoint, endpoint));

    public ClusterLayout WithUnsettled(IEnumerable<int> slots) => new(Masters, new HashSet<int>(slots));

    public IReadOnlyList<int> MovedSince(ClusterLayout other, IEnumerable<int> slots) =>
        [.. slots.Where(s => !EndPointMatch.Same(OwnerOf(s), other.OwnerOf(s)))];

    // A CLUSTER NODES line is short and comes from the server, but a match is still bounded in time.
    [System.Text.RegularExpressions.GeneratedRegex(@"\[(\d+)-[<>]-", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex TransitionPattern();
}
