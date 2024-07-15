namespace UiPath.Platform.Caching.Broadcast.Redis;

internal static class StreamConstants
{
    internal static readonly RedisValue UndeliveredMessages = ">";
    internal const string ConsumerGroupNameExistsErrorMessage = "BUSYGROUP Consumer Group name already exists";
}
