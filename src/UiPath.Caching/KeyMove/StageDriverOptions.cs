namespace UiPath.Caching.KeyMove;

internal sealed class StageDriverOptions
{
    /// <summary>The age beyond which an advert is not fresh: three refresh intervals.</summary>
    public TimeSpan AdvertFresh { get; init; } = TimeSpan.FromSeconds(6);

    public TimeSpan FlagLowerHold { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan CloseConfirm { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan AckStable { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan UpgradeHold { get; init; } = TimeSpan.FromHours(2);

    public TimeSpan DrainedSoak { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How long a transition may sit at <c>dur=0</c> before another leader step resumes it.</summary>
    public TimeSpan DurRetry { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan DryRunMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Headroom the memory plan must keep after the growth of the move.</summary>
    public double MemoryHeadroom { get; init; } = 0.2;

    public long CanaryThreshold { get; init; } = 5;

    public TimeSpan StepInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan AckInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan RepeatAlertAfter { get; init; } = TimeSpan.FromDays(1);

    /// <summary>The exact confirmation required to override the unchanged-view hold.</summary>
    public string UpgradeHoldOverrideText { get; init; } = "no node runs a binary without key move support and the pin";
}
