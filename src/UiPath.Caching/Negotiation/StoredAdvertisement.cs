namespace UiPath.Caching.Negotiation;

/// <summary>A node's advertisement, the server stamp it was written under, and how long ago that was.</summary>
internal readonly record struct StoredAdvertisement(string NodeId, NodeAdvertisement Advertisement, TimeSpan Age, long Stamp = 0);
