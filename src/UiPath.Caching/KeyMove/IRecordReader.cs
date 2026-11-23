namespace UiPath.Caching.KeyMove;

internal interface IRecordReader
{
    Task<IReadOnlyList<(RecordSide Side, int Slot, IReadOnlyDictionary<string, string> Fields)>> ReadAllAsync(string group, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> ReadSampledAsync(string group, ControlSample sample, CancellationToken cancellationToken);
}
