namespace UiPath.Caching.Tests.PhaseB.Harness;

// A stored key: a string (Text) or a hash (Fields), with an absolute expiry on the virtual clock, or -1 for none.
internal sealed class Entry
{
    public string? Text { get; init; }

    public SortedDictionary<string, string>? Fields { get; init; }

    public long ExpireAt { get; set; } = -1;

    public bool IsHash => Fields is not null;

    public static Entry Str(string text, long expireAt = -1) => new() { Text = text, ExpireAt = expireAt };

    public static Entry Hash(IEnumerable<KeyValuePair<string, string>> fields, long expireAt = -1) =>
        new() { Fields = new SortedDictionary<string, string>(fields.ToDictionary(f => f.Key, f => f.Value), StringComparer.Ordinal), ExpireAt = expireAt };

    // The value of a logical version: a string, or a two-field hash that holds the version in both fields.
    public static Entry OfVersion(string version, bool hash, long expireAt = -1) =>
        hash ? Hash([new("f1", version), new("f2", version)], expireAt) : Str(version, expireAt);

    public Entry Clone() => new() { Text = Text, Fields = Fields is null ? null : new SortedDictionary<string, string>(Fields, StringComparer.Ordinal), ExpireAt = ExpireAt };

    public string Encode() => Fields is null ? Text! : string.Join(';', Fields.Select(f => $"{f.Key}={f.Value}"));

    // The logical version a reader gets: the string, the shared field value of a complete hash, a marker for a merged hash, or one for a hash that lacks a field (a partial hash).
    public string Version()
    {
        if (Fields is null)
        {
            return Text!;
        }

        if (Fields.Count != 2 || !Fields.TryGetValue("f1", out var a) || !Fields.TryGetValue("f2", out var b))
        {
            return "!partial:" + Encode();
        }

        return a == b ? a : $"merged:{a}/{b}";
    }

    public void Write(StateHasher hasher)
    {
        hasher.Add(Text);
        hasher.Add(Fields is null ? null : Encode());
        hasher.Add(ExpireAt);
    }
}
