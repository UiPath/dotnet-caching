namespace UiPath.Platform.Caching;

public readonly struct Channel : IEquatable<Channel>
{
    public Channel()
        : this(string.Empty)
    {
    }

    public Channel(string? name) =>
        Name = name?.Trim().ToLowerInvariant() ?? string.Empty;

    public string Name { get; }

    public override bool Equals(object? obj) =>
        obj is Channel channel && Equals(channel);

    public bool Equals(Channel other) =>
        string.Equals(Name, other.Name, StringComparison.InvariantCultureIgnoreCase);

    public bool IsNull => string.IsNullOrEmpty(Name);


    public override string ToString() =>
        Name;

    public override int GetHashCode() =>
        HashCode.Combine(Name, IsNull);

    public static implicit operator string(Channel channel) =>
        channel.Name;

    public static implicit operator Channel(string? channel)
    {
        if (channel == null) return default;
        return new Channel(channel);
    }

    public static bool operator ==(Channel left, Channel right) =>
        left.Equals(right);

    public static bool operator !=(Channel left, Channel right) =>
        !(left == right);

    public static Channel Null { get; } = new Channel(null);
}
