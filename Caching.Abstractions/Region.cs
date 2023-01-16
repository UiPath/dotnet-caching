namespace UiPath.Platform.Caching;

public readonly struct Region : IEquatable<Region>
{
    public Region()
    : this(string.Empty)
    {
    }

    public Region(string? name) =>
        Name = name?.Trim().ToLowerInvariant() ?? string.Empty;

    public string Name { get; }

    public override bool Equals(object? obj) =>
        obj is Region RegionKey && Equals(RegionKey);

    public bool Equals(Region other) =>
        string.Equals(Name, other.Name, StringComparison.InvariantCultureIgnoreCase);

    public bool IsNull => string.IsNullOrEmpty(Name);


    public override string ToString() =>
        Name;

    public override int GetHashCode() =>
        HashCode.Combine(Name, IsNull);

    public static implicit operator string(Region RegionKey) =>
        RegionKey.Name;

    public static implicit operator Region(string? RegionKey)
    {
        if (RegionKey == null) return default;
        return new Region(RegionKey);
    }

    public static bool operator ==(Region left, Region right) =>
        left.Equals(right);

    public static bool operator !=(Region left, Region right) =>
        !(left == right);

    public static Region Null { get; } = new Region(null);
}
