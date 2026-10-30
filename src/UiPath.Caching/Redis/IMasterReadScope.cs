namespace UiPath.Caching.Redis;

/// <summary>A cache whose reads can be made to demand the Redis master for a stretch of code.</summary>
internal interface IMasterReadScope
{
    /// <summary>Reads this async flow makes until disposal demand the master.</summary>
    IDisposable DemandMaster();
}
