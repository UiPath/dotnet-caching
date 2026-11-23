using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The identity and state one control record holds, and, for a sample, its slot, whether any record at its epoch is committed (<see cref="Durable"/>), and whether the records at that epoch disagree (<see cref="Conflicting"/>).</summary>
internal sealed record ControlSample(long Ep, string Id, string State, string Direction, bool Durable, int Slot = -1, bool Conflicting = false)
{
    public static RedisValue[] Fields { get; } = [StateFields.Ep, StateFields.Id, StateFields.State, StateFields.Direction, StateFields.Durable];

    /// <summary>Null when the record does not exist; <paramref name="values"/> are the <see cref="Fields"/> in order.</summary>
    public static ControlSample? Parse(RedisValue[] values) =>
        values[0].IsNull || !long.TryParse((string?)values[0], out var ep)
            ? null
            : new ControlSample(ep, (string?)values[1] ?? string.Empty, (string?)values[2] ?? string.Empty, (string?)values[3] ?? string.Empty, values[4] == "1");
}
