using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The verified control records and the unchanged generations bracketing that read.</summary>
internal sealed record FinalVerification(IReadOnlyList<(RecordSide Side, RedisKey Key)> Records, GenerationPair Generations)
{
    public bool Covers(IReadOnlyList<RedisKey> srcKeys, IReadOnlyList<RedisKey> dstKeys) =>
        srcKeys.Count > 0 && dstKeys.Count > 0
        && srcKeys.All(k => Records.Contains((RecordSide.Src, k)))
        && dstKeys.All(k => Records.Contains((RecordSide.Dst, k)));
}
