namespace UiPath.Caching.PhaseB;

/// <summary>A parsed move value <c>status|token|descriptor|cert</c>, or the malformed marker.</summary>
internal sealed record ClaimValue(string Status, string Token, string Descriptor, string Cert)
{
    public static readonly ClaimValue Malformed = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public bool IsMalformed => ReferenceEquals(this, Malformed);

    public bool IsLive => Status is ClaimGrammar.Pending or ClaimGrammar.Open;

    public string Text => $"{Status}|{Token}|{Descriptor}|{Cert}";
}
