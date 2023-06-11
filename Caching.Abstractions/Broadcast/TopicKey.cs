namespace UiPath.Platform.Caching.Broadcast;

public readonly struct TopicKey : IEquatable<TopicKey>
{
    public TopicKey()
        : this(string.Empty)
    {
    }

    public TopicKey(string? name) =>
        Name = name?.Trim().ToLowerInvariant() ?? string.Empty;

    public string Name { get; }

    public override bool Equals(object? obj) =>
        obj is TopicKey topicKey && Equals(topicKey);

    public bool Equals(TopicKey other) =>
        string.Equals(Name, other.Name, StringComparison.InvariantCultureIgnoreCase);

    public bool IsNull => string.IsNullOrEmpty(Name);


    public override string ToString() =>
        Name;

    public override int GetHashCode() =>
        HashCode.Combine(Name, IsNull);

    public static implicit operator string(TopicKey topicKey) =>
        topicKey.Name;

    public static implicit operator TopicKey(string? value)
    {
        if (value == null) return default;
        return new TopicKey(value);
    }

    public static bool operator ==(TopicKey left, TopicKey right) =>
        left.Equals(right);

    public static bool operator !=(TopicKey left, TopicKey right) =>
        !(left == right);

    public static TopicKey Null { get; } = new TopicKey(null);
}
