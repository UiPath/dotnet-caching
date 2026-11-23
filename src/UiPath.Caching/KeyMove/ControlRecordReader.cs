using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed class ControlRecordReader(IDatabase controlDatabase, MoveKeyNames names) : IRecordReader
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

    async Task<IReadOnlyList<(RecordSide Side, int Slot, IReadOnlyDictionary<string, string> Fields)>> IRecordReader.ReadAllAsync(string group, CancellationToken cancellationToken) =>
        [.. (await ReadAllAsync(group, cancellationToken).ConfigureAwait(false)).Select(static r => (RecordSide.Src, r.Slot, r.Fields))];

    Task<IReadOnlyDictionary<string, string>> IRecordReader.ReadSampledAsync(string group, ControlSample sample, CancellationToken cancellationToken) =>
        ReadRecordAsync(controlDatabase, names.Control(group, sample.Slot));

    internal static async Task<IReadOnlyDictionary<string, string>> ReadRecordAsync(IDatabase database, RedisKey key) =>
        (await database.HashGetAllAsync(key, Master).ConfigureAwait(false)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
}
