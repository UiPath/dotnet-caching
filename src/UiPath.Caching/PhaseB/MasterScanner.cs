using System.Runtime.CompilerServices;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>SCAN on every master through the server API, which does not apply the connector prefix: the pattern starts with it and each result has it stripped.</summary>
internal sealed class MasterScanner(Func<IEnumerable<IServer>> servers, int database, string connectorPrefix, int pageSize = 500)
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    public async IAsyncEnumerable<string> ScanAsync(IReadOnlyList<string> patterns, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var masters = servers().Where(s => s.IsConnected && !s.IsReplica).ToArray();
        if (masters.Length == 0)
        {
            throw new RedisException("No master is connected, so the keys cannot be scanned on one.");
        }

        var seen = patterns.Count > 1 ? new HashSet<string>(StringComparer.Ordinal) : null;
        foreach (var server in masters)
        {
            foreach (var pattern in patterns)
            {
                await foreach (var key in server.KeysAsync(database, EscapeGlob(connectorPrefix) + pattern, pageSize, 0, 0, Master).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    var text = (string?)key;
                    if (text is null || !text.StartsWith(connectorPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var logical = text[connectorPrefix.Length..];
                    if (seen is null || seen.Add(logical))
                    {
                        yield return logical;
                    }
                }
            }
        }
    }

    internal static string EscapeGlob(string text)
    {
        if (text.AsSpan().IndexOfAny("\\*?[]") < 0)
        {
            return text;
        }

        var escaped = new System.Text.StringBuilder(text.Length + 4);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '?' or '[' or ']')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
