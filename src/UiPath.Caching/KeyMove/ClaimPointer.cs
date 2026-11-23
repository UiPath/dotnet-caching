namespace UiPath.Caching.KeyMove;

/// <summary>A parsed pointer <c>group|token|toId|fromId</c>, the empty pointer, or the malformed marker.</summary>
internal sealed record ClaimPointer(string Group, string Token, string ToId, string FromId)
{
    public static readonly ClaimPointer Malformed = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public static readonly ClaimPointer Empty = new(ClaimGrammar.NoPointer, string.Empty, string.Empty, string.Empty);

    public bool IsMalformed => ReferenceEquals(this, Malformed);
}
