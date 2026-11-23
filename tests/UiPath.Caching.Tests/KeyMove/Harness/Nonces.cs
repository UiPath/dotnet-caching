namespace UiPath.Caching.Tests.KeyMove.Harness;

internal static class Nonces
{
    // A nonce is unique per actor and step, and has a fixed length like a real 128-bit hex nonce.
    public static string Of(string actor, int sequence) => $"{actor}.{sequence}".PadRight(16, '~');
}
