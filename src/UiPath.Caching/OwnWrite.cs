namespace UiPath.Caching;

/// <summary>A write of this node whose fields a local entry already holds; its version is zero until the write returns.</summary>
internal sealed class OwnWrite(string[] keys, bool replace = false)
{
    public string[] Keys { get; } = keys;

    /// <summary>The write replaced the whole hash, so the entry is exactly the state at its version.</summary>
    public bool Replace { get; } = replace;

    public long Version { get; set; }

    public bool Failed { get; set; }
}
