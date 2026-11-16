using System.Text;

namespace UiPath.Caching.Redis;

/// <summary>What a counted hash write tells its caller: the write count it reached, and the fields a peer can apply.</summary>
internal sealed class WriteReport(PeerFieldLimits limits)
{
    public long Version { get; set; }

    public IReadOnlyDictionary<string, byte[]?>? Fields { get; private set; }

    /// <summary>Only a merge within the limits is carried: a replacement leaves nothing a peer could add its fields to.</summary>
    public void Capture(HashEntry[] entries, HashCacheSetOption setOption)
    {
        if (setOption != HashCacheSetOption.HashReplace)
        {
            return;
        }

        var fields = new PeerFieldBytes();
        var bytes = 0;
        foreach (var entry in entries)
        {
            var name = entry.Name.ToString();
            if (name == KnownFieldNames.MetadataKey)
            {
                continue;
            }

            // Measured before it is copied, so a peer's oversized field costs nothing.
            var held = (ReadOnlyMemory<byte>)entry.Value;
            bytes += Encoding.UTF8.GetByteCount(name) + held.Length;
            if (fields.Count == limits.MaxFields || bytes > limits.MaxBytes)
            {
                return;
            }

            // A copy: the entry may lend memory that is reclaimed once the write returns, and the event goes out after it.
            fields[name] = held.ToArray();
        }

        if (fields.Count > 0)
        {
            Fields = fields;
        }
    }
}
