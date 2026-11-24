namespace UiPath.Caching.KeyMove;

/// <summary>Tells from the stored length of a value whether a move would refuse it for good, so it is dropped instead of moved.</summary>
internal interface IValueClassifier
{
    /// <summary>True when a value of <paramref name="length"/> stored bytes, in a key of <paramref name="cacheId"/> on <paramref name="side"/>, is one the codec refuses and a delete may remove.</summary>
    bool IsDroppable(string cacheId, MoveSide side, long length);
}
