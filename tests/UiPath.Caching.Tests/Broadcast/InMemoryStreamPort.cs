using System.Globalization;
using StackExchange.Redis;
using UiPath.Caching.Broadcast.Redis;

namespace UiPath.Caching.Tests.Broadcast;

/// <summary>
/// A stream with one group, in memory. An operation can be held until the test releases or fails it, and runs against
/// the state as of its release. A check sent with a read is one <see cref="Op.Read"/>, held with it and answered from
/// the state the read runs against, as Redis runs the two back to back.
/// </summary>
internal sealed class InMemoryStreamPort(RedisValue group, RedisValue field) : IStreamPort
{
    private readonly object _lock = new();
    private readonly List<(long Seq, string Payload)> _log = [];
    private readonly SortedSet<long> _pending = [];
    private readonly List<string> _acknowledged = [];
    private readonly Queue<Held>[] _holds = [new(), new(), new(), new(), new()];
    private readonly Exception?[] _failures = new Exception?[5];
    private readonly int[] _calls = new int[5];
    private int _scriptReads;
    private long _added;
    private bool _exists;
    private bool _hasGroup;
    private long _delivered;
    private long? _read;

    internal enum Op
    {
        Read,
        CreateGroup,
        Acknowledge,
        Info,
        Groups,
    }

    /// <summary>Whether <c>XINFO STREAM</c> carries the counts Redis 7.0 added; off, it answers as a server below 7.0.</summary>
    public bool HasCounts { get; set; } = true;

    public string[] Acknowledged
    {
        get
        {
            lock (_lock)
            {
                return [.. _acknowledged];
            }
        }
    }

    public bool HasGroup
    {
        get
        {
            lock (_lock)
            {
                return _hasGroup;
            }
        }
    }

    public string LastDelivered
    {
        get
        {
            lock (_lock)
            {
                return Id(_delivered);
            }
        }
    }

    /// <summary>Reads that went through the check-and-read script.</summary>
    public int ScriptReads => Volatile.Read(ref _scriptReads);

    /// <summary>How often <paramref name="op"/> was called; <see cref="Op.Info"/> and <see cref="Op.Groups"/> count the checks sent with a read.</summary>
    public int Calls(Op op) => Volatile.Read(ref _calls[(int)op]);

    /// <summary>Holds the next <paramref name="op"/> to start, before it touches the stream; a check is held with its read.</summary>
    public Held Hold(Op op)
    {
        var held = new Held();
        lock (_lock)
        {
            _holds[(int)op].Enqueue(held);
        }

        return held;
    }

    /// <summary>Fails every <paramref name="op"/> from now on with <paramref name="failure"/>; <c>null</c> ends it. A check that fails leaves its read to run.</summary>
    public void FailWith(Op op, Exception? failure)
    {
        lock (_lock)
        {
            _failures[(int)op] = failure;
        }
    }

    public void Add(string payload)
    {
        lock (_lock)
        {
            _exists = true;
            _log.Add((++_added, payload));
        }
    }

    /// <summary>Removes the oldest <paramref name="count"/> entries the way a trim does: the counts show it, max-deleted-entry-id does not.</summary>
    public void Trim(int count)
    {
        lock (_lock)
        {
            _log.RemoveRange(0, Math.Min(count, _log.Count));
        }
    }

    public void DropGroup()
    {
        lock (_lock)
        {
            _hasGroup = false;
            _pending.Clear();
        }
    }

    /// <summary>Moves the group to the head, as another actor's <c>XGROUP SETID $</c>, or a group it made anew there, would.</summary>
    public void MoveGroupToHead()
    {
        lock (_lock)
        {
            _delivered = _added;
            _read = _added;
            _pending.Clear();
        }
    }

    public Task<StreamEntry[]> ReadGroupAsync(RedisValue position, bool noAck) =>
        RunAsync(Op.Read, () => ReadCore(position, noAck));

    public Task<RedisResult> CheckAndReadAsync(bool full)
    {
        Interlocked.Increment(ref _scriptReads);
        return RunAsync(Op.Read, () => CheckAndReadCore(full));
    }

    public CheckedRead CheckThenRead(bool full, bool noAck)
    {
        lock (_lock)
        {
            _calls[(int)Op.Info]++;
            if (full)
            {
                _calls[(int)Op.Groups]++;
            }
        }

        var run = RunAsync(Op.Read, () => CheckThenReadCore(full, noAck));
        return new(Part(run, static p => p.Stream), Part(run, static p => p.Group), Part(run, static p => p.Entries));
    }

    public Task<StreamEntry[]> RangeAsync(RedisValue minId, RedisValue maxId, int count) => Task.FromResult<StreamEntry[]>([]);

    /// <summary>Unknown, as when scripts are refused.</summary>
    public Task<StreamHead?> HeadAsync() => Task.FromResult<StreamHead?>(null);

    public Task<bool> CreateGroupAsync(RedisValue position, StreamHead? head) =>
        RunAsync(Op.CreateGroup, () => CreateCore(position));

    public Task<long> AcknowledgeAsync(RedisValue[] ids) =>
        RunAsync(Op.Acknowledge, () => AcknowledgeCore(ids));

    private static string Id(long seq) => $"{seq}-0";

    private static long Seq(RedisValue id) => long.Parse(((string)id!).Split('-')[0], CultureInfo.InvariantCulture);

    private static async Task<T> Part<T>(Task<Pair> run, Func<Pair, Func<T>> part) => part(await run.ConfigureAwait(false))();

    /// <summary>What <paramref name="action"/> returns or throws, kept for the part of a pair that asks for it.</summary>
    private static Func<T> Outcome<T>(Func<T> action)
    {
        try
        {
            var result = action();
            return () => result;
        }
        catch (Exception ex)
        {
            return () => throw ex;
        }
    }

    private async Task<T> RunAsync<T>(Op op, Func<T> action)
    {
        Held? held = null;
        lock (_lock)
        {
            _calls[(int)op]++;
            if (_holds[(int)op].Count > 0)
            {
                held = _holds[(int)op].Dequeue();
            }
        }

        if (held is not null)
        {
            await held.ArriveAsync().ConfigureAwait(false);
        }

        try
        {
            lock (_lock)
            {
                if (_failures[(int)op] is { } failure)
                {
                    throw failure;
                }

                return action();
            }
        }
        finally
        {
            held?.Complete();
        }
    }

    private StreamEntry[] ReadCore(RedisValue position, bool noAck)
    {
        if (!_hasGroup)
        {
            throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOGROUP No such key or consumer group");
        }

        if (position != StreamConstants.UndeliveredMessages)
        {
            var from = Seq(position);
            return [.. _pending.Where(s => s > from).Select(PendingEntry)];
        }

        var read = _log.Where(e => e.Seq > _delivered).Take(100).ToArray();
        foreach (var entry in read)
        {
            _delivered = entry.Seq;
            _read = entry.Seq;
            if (!noAck)
            {
                _pending.Add(entry.Seq);
            }
        }

        return [.. read.Select(Entry)];
    }

    // A pending entry since removed from the stream comes back without fields.
    private StreamEntry PendingEntry(long seq) =>
        _log.Exists(e => e.Seq == seq) ? Entry(_log.Find(e => e.Seq == seq)) : new StreamEntry(Id(seq), []);

    private StreamEntry Entry((long Seq, string Payload) e) => new(Id(e.Seq), [new NameValueEntry(field, e.Payload)]);

    private RedisResult CheckAndReadCore(bool full)
    {
        if (!_exists)
        {
            throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ERR no such key");
        }

        var stream = new List<RedisResult>
        {
            RedisResult.Create((RedisValue)"length"), RedisResult.Create((RedisValue)_log.Count),
            RedisResult.Create((RedisValue)"last-generated-id"), RedisResult.Create((RedisValue)Id(_added)),
            RedisResult.Create((RedisValue)"max-deleted-entry-id"), RedisResult.Create((RedisValue)"0-0"),
            RedisResult.Create((RedisValue)"entries-added"), RedisResult.Create((RedisValue)_added),
        };
        if (_log.Count > 0)
        {
            stream.Add(RedisResult.Create((RedisValue)"first-entry"));
            stream.Add(RedisResult.Create([RedisResult.Create((RedisValue)Id(_log[0].Seq))]));
        }

        var groups = full && _hasGroup
            ? new[] { RedisResult.Create(new RedisValue[] { "name", group, "last-delivered-id", Id(_delivered), "entries-read", _read is { } r ? r : RedisValue.Null }) }
            : [];
        var entries = ReadCore(StreamConstants.UndeliveredMessages, noAck: false);
        return RedisResult.Create(
        [
            RedisResult.Create([.. stream]),
            RedisResult.Create(groups),
            entries.Length == 0
                ? RedisResult.Create(RedisValue.Null)
                : RedisResult.Create([RedisResult.Create([RedisResult.Create((RedisValue)"stream"), RedisResult.Create([.. entries.Select(Reply)])])]),
        ]);
    }

    // The check's parts answer as XINFO would at the instant of the read: the stream's state the client parses, and the
    // group's position with its count read as StackExchange.Redis reads a nil, 0, which the check takes as unknown.
    private Pair CheckThenReadCore(bool full, bool noAck)
    {
        var stream = Outcome(() =>
        {
            Check(Op.Info);
            return new StreamState(_log.Count, Id(_log.Count == 0 ? _added : _log[0].Seq), Id(_added), HasCounts ? "0-0" : RedisValue.Null, HasCounts ? _added : 0);
        });
        var position = full
            ? Outcome<GroupPosition?>(() =>
            {
                Check(Op.Groups);
                return _hasGroup ? new GroupPosition(Id(_delivered), HasCounts && _read is > 0 ? _read : null) : null;
            })
            : static () => null;
        var entries = Outcome(() => ReadCore(StreamConstants.UndeliveredMessages, noAck));
        return new(stream, position, entries);
    }

    private void Check(Op op)
    {
        if (_failures[(int)op] is { } failure)
        {
            throw failure;
        }

        if (!_exists)
        {
            throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ERR no such key");
        }
    }

    private RedisResult Reply(StreamEntry e) =>
        RedisResult.Create([RedisResult.Create(e.Id), RedisResult.Create(new RedisValue[] { field, e[field] })]);

    private bool CreateCore(RedisValue position)
    {
        if (_hasGroup)
        {
            throw new RedisServerException(RedisErrorKind.None, CommandFlags.None, StreamConstants.ConsumerGroupNameExistsErrorMessage);
        }

        _exists = true;
        _hasGroup = true;
        _pending.Clear();
        var atHead = position == StreamPosition.NewMessages;
        _delivered = atHead ? _added : Seq(position);
        _read = atHead ? _added : null;
        return true;
    }

    private long AcknowledgeCore(RedisValue[] ids)
    {
        foreach (var id in ids)
        {
            _pending.Remove(Seq(id));
            _acknowledged.Add(id!);
        }

        return ids.Length;
    }

    /// <summary>The parts of a check sent with a read, each answering or throwing as the server would have.</summary>
    private sealed record Pair(Func<StreamState> Stream, Func<GroupPosition?> Group, Func<StreamEntry[]> Entries);

    internal sealed class Held
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the operation has arrived and waits.</summary>
        public Task Reached => _reached.Task;

        /// <summary>Completes once the released operation has run against the stream, with its result or its failure; what a test changes after that is for the next one.</summary>
        public Task Completed => _completed.Task;

        public void Release() => _released.TrySetResult();

        public void Fail(Exception failure) => _released.TrySetException(failure);

        internal Task ArriveAsync()
        {
            _reached.TrySetResult();
            return _released.Task;
        }

        internal void Complete() => _completed.TrySetResult();
    }
}
