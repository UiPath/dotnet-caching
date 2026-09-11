using System.Net;

namespace UiPath.Caching.Redis;

/// <summary>What a topology refresh reported; when conclusive, a null <see cref="Members"/> means membership can never be judged.</summary>
internal readonly record struct ClusterMembership(bool Conclusive, HashSet<EndPoint>? Members)
{
    public static ClusterMembership Inconclusive => default;
}
