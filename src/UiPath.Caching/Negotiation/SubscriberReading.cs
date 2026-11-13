namespace UiPath.Caching.Negotiation;

/// <summary>
/// What the Pub/Sub node list found on its latest read, after the debounce: whether it could count at all, whether subscribers no advertisement accounts for
/// have shown in two views running, whether the count has been clean and unchanged for two, and what is surplus now. <paramref name="Short"/> is the other
/// way round: fewer subscribers than the advertisements claim, in two views running, which is a node that went away without saying so, and so a count
/// that does not add up; <paramref name="ShortChannels"/> names those channels. <paramref name="Replaced"/> is a confirmed deficit that ended while the claims
/// behind it are still fresh, so the subscriber count rose: something subscribed, which is a join and not the claim expiring. <paramref name="Why"/> says why
/// the count is unknown.
/// </summary>
internal sealed record SubscriberReading(
    bool Known,
    bool Unaccounted,
    bool Settled,
    long Surplus,
    IReadOnlyList<string> Channels,
    bool Short = false,
    IReadOnlyList<string>? ShortChannels = null,
    bool Replaced = false,
    string? Why = null)
{
    public static SubscriberReading Unknown { get; } = new(false, false, false, 0, []);
}
