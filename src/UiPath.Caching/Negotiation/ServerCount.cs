namespace UiPath.Caching.Negotiation;

/// <summary>What one server counts: subscribers of each channel asked, and the patterns subscribed to on the whole server.</summary>
internal sealed record ServerCount(IReadOnlyList<long> Channels, long Patterns);
