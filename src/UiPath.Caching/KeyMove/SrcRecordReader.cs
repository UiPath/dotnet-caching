using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed class SrcRecordReader(IDatabase srcDatabase, MoveKeyNames names)
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    public async Task<ControlSample?> ReadAsync(string group, CancellationToken cancellationToken)
    {
        var (slot, key) = names.ControlRecords(group)[0];
        var read = ControlSample.Parse(await srcDatabase.HashGetAsync(key, ControlSample.Fields, Master).WaitAsync(cancellationToken).ConfigureAwait(false));
        return read is null ? null : read with { Slot = slot, SrcHome = true, SrcDurable = read.Durable };
    }
}
