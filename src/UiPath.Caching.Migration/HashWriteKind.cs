namespace UiPath.Caching.Migration;

/// <summary>What a hash write does, which with the state decides its steps.</summary>
internal enum HashWriteKind
{
    /// <summary>The whole hash is replaced.</summary>
    Replace,

    /// <summary>The fields are merged into the hash.</summary>
    Merge,

    /// <summary>The key is deleted.</summary>
    Delete,

    /// <summary>Some fields are deleted.</summary>
    DeleteFields,
}
