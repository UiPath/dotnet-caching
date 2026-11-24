using System.Net;
using System.Reflection;
using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>A server that records the cursor of every SCAN sent to it, can pose as another endpoint, and can fail its scans.</summary>
public class ScanRecordingServer : DispatchProxy
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly List<string> _cursors = [];
    private IServer _inner = default!;
    private EndPoint? _pose;
    private bool _disconnected;

    public bool Fail { get; set; }

    /// <summary>How INFO answers: as the server does, with an error, or with no run id.</summary>
    public InfoFault Info { get; set; }

    /// <summary>When set, INFO names this run id instead of the server's own.</summary>
    public string? RunId { get; set; }

    public Exception? ScanFailure { get; set; }

    public bool DisconnectOnFailure { get; set; }

    public Func<string, RedisResult>? ScanReply { get; set; }

    public int FailAfter { get; set; } = int.MaxValue;

    public Func<IReadOnlyList<object>, RedisResult>? Reply { get; set; }

    public IReadOnlyList<string> Cursors
    {
        get
        {
            lock (_gate)
            {
                return [.. _cursors];
            }
        }
    }

    public static IServer Over(IServer inner, out ScanRecordingServer recorder, EndPoint? poseAs = null)
    {
        var proxy = Create<IServer, ScanRecordingServer>();
        recorder = (ScanRecordingServer)(object)proxy;
        recorder._inner = inner;
        recorder._pose = poseAs;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (_pose is not null && targetMethod!.Name == "get_EndPoint")
        {
            return _pose;
        }

        if (_disconnected && targetMethod!.Name == "get_IsConnected")
        {
            return false;
        }

        if (targetMethod!.Name == "InfoAsync" && (Info != InfoFault.None || RunId is not null))
        {
            return Info switch
            {
                InfoFault.Fail => Task.FromException<IGrouping<string, KeyValuePair<string, string>>[]>(new RedisException("INFO failed.")),
                InfoFault.NoRunId => Task.FromResult(new[] { new KeyValuePair<string, string>("redis_version", "7.0.0") }.GroupBy(_ => "Server").ToArray()),
                _ => Task.FromResult(new[] { new KeyValuePair<string, string>("run_id", RunId!) }.GroupBy(_ => "Server").ToArray()),
            };
        }

        if (targetMethod.Name == "ExecuteAsync" && args is [int, "SCAN", ICollection<object> scan, ..] && RecordScan(scan) is { } recorded)
        {
            return Task.FromResult(recorded);
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private RedisResult? RecordScan(ICollection<object> scan)
    {
        var cursor = (string)scan.First();
        int seen;
        lock (_gate)
        {
            _cursors.Add(cursor);
            seen = _cursors.Count;
        }

        if (Fail || seen > FailAfter)
        {
            _disconnected = DisconnectOnFailure;
            throw ScanFailure ?? new RedisException("The scan failed.");
        }

        return Reply?.Invoke(scan.ToArray()) ?? ScanReply?.Invoke(cursor);
    }
}
