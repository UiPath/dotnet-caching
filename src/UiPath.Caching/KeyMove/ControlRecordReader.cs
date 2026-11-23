using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed class ControlRecordReader(IDatabase controlDatabase, MoveKeyNames names)
{
    private const int Batch = 512;
    private const CommandFlags Master = CommandFlags.DemandMaster;

    public async Task<IReadOnlyList<(int Slot, RedisValue[] Values)>> ReadFieldsAsync(string group, RedisValue[] fields, CancellationToken cancellationToken)
    {
        var result = new List<(int, RedisValue[])>();
        foreach (var chunk in names.SlotSet().Chunk(Batch))
        {
            var reads = chunk.Select(slot => (slot, read: controlDatabase.HashGetAsync(names.Control(group, slot), fields, Master))).ToArray();
            await Task.WhenAll(reads.Select(r => r.read)).WaitAsync(cancellationToken).ConfigureAwait(false);
            result.AddRange(reads.Select(r => (r.slot, r.read.Result)));
        }

        return result;
    }

    public async Task<IReadOnlyList<(int Slot, IReadOnlyDictionary<string, string> Fields)>> ReadAllAsync(string group, CancellationToken cancellationToken)
    {
        var result = new List<(int, IReadOnlyDictionary<string, string>)>();
        foreach (var chunk in names.SlotSet().Chunk(Batch))
        {
            var reads = chunk.Select(slot => (slot, read: controlDatabase.HashGetAllAsync(names.Control(group, slot), Master))).ToArray();
            await Task.WhenAll(reads.Select(r => r.read)).WaitAsync(cancellationToken).ConfigureAwait(false);
            result.AddRange(reads.Where(r => r.read.Result.Length > 0).Select(r => (r.slot, (IReadOnlyDictionary<string, string>)r.read.Result.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString()))));
        }

        return result;
    }
}
