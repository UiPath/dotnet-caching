namespace UiPath.Caching.Negotiation;

/// <summary>
/// A registered serializer a policy may write, and the tokens a node must read to be sent it. <paramref name="Policies"/> are the registered policies
/// that write it, and <paramref name="Others"/> the registered policies that write something else, whose read lists say nothing about it.
/// </summary>
internal sealed record DesiredWrite(ICacheValueSerializer Writer, IReadOnlyList<string> Tokens, IReadOnlyCollection<string> Policies, IReadOnlyCollection<string> Others);
