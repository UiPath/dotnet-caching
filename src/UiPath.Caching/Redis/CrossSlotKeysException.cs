namespace UiPath.Caching.Redis;

public class CrossSlotKeysException : Exception
{
    public CrossSlotKeysException(string? message) : base(message)
    {
    }

    public CrossSlotKeysException()
    {
    }

    public CrossSlotKeysException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
