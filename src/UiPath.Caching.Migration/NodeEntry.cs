namespace UiPath.Caching.Migration;

/// <summary>One field of the node hash: the text after the server stamp, and how long ago it was written.</summary>
internal readonly record struct NodeEntry(string Field, string Text, TimeSpan Age);
