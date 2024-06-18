namespace UiPath.Platform.Caching.Redis;
internal struct StreamId
{
    public long Timestamp { get; private set; }
    public long Sequence { get; private set; }
    public bool Valid { get; private set; }
    public static readonly StreamId Invalid = new StreamId();
    public StreamId()
    {
        Timestamp = 0;
        Sequence = 0;
        Valid = false;
    }
    public StreamId(long timestamp, long sequence)  
    {
        Timestamp = timestamp;
        Sequence = sequence;
        Valid = true;
    }
}
