namespace UiPath.Platform.Caching;
public enum HashCacheSetOption
{
    ///<summary>
    /// Option to set the specified fields to their respective values in the hash stored at key.
    /// This option overwrites any specified fields that already exist in the hash, leaving other unspecified fields untouched.
    /// </summary>
    HashReplace,
    ///<summary>
    /// Option to remove the entire specified hash key and set the specified fields to their respective values in the hash stored at key.
    /// </summary>
    KeyReplace
}
