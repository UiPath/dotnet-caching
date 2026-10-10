namespace UiPath.Caching;

/// <summary>Lets the work a caller starts keep that caller's locks past the caller's own return, so a lock never frees while work it guards still runs.</summary>
internal sealed class LockLifetime
{
    public IInFlightRun? Run { get; set; }
}
