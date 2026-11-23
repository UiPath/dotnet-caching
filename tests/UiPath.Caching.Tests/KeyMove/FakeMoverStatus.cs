using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

internal sealed class FakeMoverStatus : IMoverStatus
{
    public DrainReport? Report { get; set; }

    public IReadOnlyDictionary<string, long> Inventory { get; set; } = new Dictionary<string, long>();

    public List<string> Directions { get; } = [];

    public static DrainReport Drained(long canary = 0, params (string Cache, long Count)[] residue) =>
        new(true, residue.ToDictionary(r => r.Cache, r => r.Count), canary);

    public Task<DrainReport?> DrainAsync(string group, string direction, CancellationToken cancellationToken)
    {
        Directions.Add(direction);
        return Task.FromResult(Report);
    }

    public Task<IReadOnlyDictionary<string, long>> ResidueInventoryAsync(string group, CancellationToken cancellationToken) => Task.FromResult(Inventory);
}
