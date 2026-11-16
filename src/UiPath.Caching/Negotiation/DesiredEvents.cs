namespace UiPath.Caching.Negotiation;

/// <summary>The event format a node is set to write, and the tokens a node must read to be sent it.</summary>
internal sealed record DesiredEvents(IReadOnlyList<string> Tokens);
