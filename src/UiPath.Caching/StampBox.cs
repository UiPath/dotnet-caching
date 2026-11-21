namespace UiPath.Caching;

/// <summary>What an adoption read saw, for the store that follows the generator to compare against.</summary>
internal sealed class StampBox
{
    /// <summary>Whether the read answered, so the store may compare against <see cref="Stamp"/> (null for an absent key).</summary>
    public bool Read { get; set; }

    public object? Stamp { get; set; }

    public long Mark { get; set; }
}
