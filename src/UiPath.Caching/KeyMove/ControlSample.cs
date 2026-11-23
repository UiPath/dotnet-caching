using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The identity and state one control record holds, and, for a sample, its slot, whether any record at its epoch is committed (<see cref="Durable"/>), and whether the records at that epoch disagree (<see cref="Conflicting"/>).</summary>
/// <param name="NPop">The record's <c>nPop</c>; -1 when the record does not carry it.</param>
/// <param name="SrcHome">An instance sample whose identity is also held by the record of the Src (the Src is its home).</param>
/// <param name="SrcDurable">The Src record of that identity shows <c>dur=1</c>; always false when <paramref name="SrcHome"/> is false.</param>
internal sealed record ControlSample(long Ep, string Id, string State, string Direction, bool Durable, int Slot = -1, bool Conflicting = false, int NPop = -1, bool SrcHome = false, bool SrcDurable = false)
{
    public static RedisValue[] Fields { get; } = [StateFields.Ep, StateFields.Id, StateFields.State, StateFields.Direction, StateFields.Durable, StateFields.RecordNPop];

    /// <summary>Null when the record does not exist; <paramref name="values"/> are the <see cref="Fields"/> in order, the last one optional.</summary>
    public static ControlSample? Parse(RedisValue[] values)
    {
        if (values[0].IsNull || !long.TryParse(values[0].ToString(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var ep))
        {
            return null;
        }

        var nPop = values.Length > 5 && int.TryParse(values[5].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var stored) ? stored : -1;
        return new ControlSample(ep, values[1].ToString(), values[2].ToString(), values[3].ToString(), values[4] == "1", NPop: nPop);
    }
}
