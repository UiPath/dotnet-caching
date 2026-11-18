namespace UiPath.Caching.Migration;

/// <summary>Set by the serializer decorator during one library call, since the public cache API reports an unreadable value as a plain miss and a serializer failure as a failed write.</summary>
internal sealed class ReadProbe
{
    private static readonly AsyncLocal<ReadProbe?> Active = new();

    private int _unreadable;
    private int _serializeFailed;

    public static ReadProbe? Current => Active.Value;

    public bool Unreadable => Volatile.Read(ref _unreadable) != 0;

    public bool SerializeFailed => Volatile.Read(ref _serializeFailed) != 0;

    public static ReadProbe Begin()
    {
        var probe = new ReadProbe();
        Active.Value = probe;
        return probe;
    }

    public static void End() => Active.Value = null;

    public void MarkUnreadable() => Volatile.Write(ref _unreadable, 1);

    public void MarkSerializeFailed() => Volatile.Write(ref _serializeFailed, 1);
}
