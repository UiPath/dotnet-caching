namespace UiPath.Caching.Redis;

/// <summary>Decides when a policy's desired <see cref="SerializerPolicy.Write"/> format is safe to write, from what the nodes advertise.</summary>
public sealed class FormatNegotiationOptions
{
    public FormatNegotiationMode Mode { get; set; } = FormatNegotiationMode.Auto;

    /// <summary>How often this node refreshes its advertisement; an advertisement counts for <see cref="LivenessWindow"/> plus two of these.</summary>
    public TimeSpan AdvertiseInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often this node reads the node list.</summary>
    public TimeSpan ViewInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a node without an advertisement counts as live after its consumer last read its stream.</summary>
    public TimeSpan LivenessWindow { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How long every node must have been accounted for, unchanged, before a format is written; zero switches at once, and a node that cannot read it stops writes at once. An hour by default, so a rollback within it never wrote the format.</summary>
    public TimeSpan UpgradeHold { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a decision survives a node list that cannot be read, before writes fall back to JSON.</summary>
    public TimeSpan MaxViewAge { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Attests that every node the node list cannot see (broadcast off, or no Streams topic) reads the desired formats; off by default, so such an app writes JSON.</summary>
    public bool AssumeAllNodesCanRead { get; set; }

    internal FormatNegotiationOptions Clone() => (FormatNegotiationOptions)MemberwiseClone();

    internal void Validate()
    {
        Positive(AdvertiseInterval, nameof(AdvertiseInterval));
        Positive(ViewInterval, nameof(ViewInterval));
        Positive(LivenessWindow, nameof(LivenessWindow));
        Positive(MaxViewAge, nameof(MaxViewAge));
        if (UpgradeHold < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"RedisCacheOptions.FormatNegotiation.{nameof(UpgradeHold)} must not be negative; it is {UpgradeHold}.");
        }
    }

    private static void Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"RedisCacheOptions.FormatNegotiation.{name} must be positive; it is {value}.");
        }
    }
}
