namespace UiPath.Caching.Broadcast;

public readonly struct TopicKey : IEquatable<TopicKey>
{
    public TopicKey()
        : this(string.Empty)
    {
    }

    public TopicKey(string? name) =>
        Name = name?.Trim().ToLowerInvariant() ?? string.Empty;

    public static TopicKey Null { get; } = new TopicKey(null);

    public string Name { get; }

    public bool IsNull => string.IsNullOrEmpty(Name);

    public static implicit operator string(TopicKey topicKey) =>
        topicKey.Name;

    public static implicit operator TopicKey(string? value)
    {
        if (value == null)
        {
            return default;
        }

        return new TopicKey(value);
    }

    public static bool operator ==(TopicKey left, TopicKey right) =>
        left.Equals(right);

    public static bool operator !=(TopicKey left, TopicKey right) =>
        !(left == right);

    public override bool Equals(object? obj) =>
        obj is TopicKey topicKey && Equals(topicKey);

    public bool Equals(TopicKey other) =>
        string.Equals(Name, other.Name, StringComparison.InvariantCultureIgnoreCase);


    public override string ToString() =>
        Name;

    public override int GetHashCode() =>
        HashCode.Combine(Name, IsNull);
}
