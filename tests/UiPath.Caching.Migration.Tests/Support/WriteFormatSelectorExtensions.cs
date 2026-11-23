namespace UiPath.Caching.Migration.Tests.Support;

internal static class WriteFormatSelectorExtensions
{
    /// <summary>The policy of one write, taken and released at once, for a test that writes nothing with it.</summary>
    public static CachePolicy PolicyFor(this WriteFormatSelector selector, GroupPin pin)
    {
        using var lease = selector.Select(pin);
        return lease.Policy!;
    }
}
