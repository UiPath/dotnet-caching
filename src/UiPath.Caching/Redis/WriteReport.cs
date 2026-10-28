using System.Text;

namespace UiPath.Caching.Redis;

/// <summary>What a counted hash write tells its caller: the write count it reached, and the fields a peer can apply.</summary>
internal sealed class WriteReport(PeerFieldLimits limits)
{
    public long Version { get; set; }

    public IDictionary<string, string?>? Fields { get; private set; }

    /// <summary>Only a merge within the limits is carried: a replacement leaves nothing a peer could add its fields to.</summary>
    public void Capture(HashEntry[] entries, HashCacheSetOption setOption)
    {
        if (setOption != HashCacheSetOption.HashReplace)
        {
            return;
        }

        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        var bytes = 0;
        foreach (var entry in entries)
        {
            var name = entry.Name.ToString();
            if (name == KnownFieldNames.MetadataKey)
            {
                continue;
            }

            var value = (byte[]?)entry.Value ?? [];
            bytes += Encoding.UTF8.GetByteCount(name) + value.Length;
            if (fields.Count == limits.MaxFields || bytes > limits.MaxBytes)
            {
                return;
            }

            fields[name] = Convert.ToBase64String(value);
        }

        if (fields.Count > 0)
        {
            Fields = fields;
        }
    }
}
