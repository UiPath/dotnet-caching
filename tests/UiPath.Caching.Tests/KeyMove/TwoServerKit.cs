using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>What one test of an instance move needs on two servers: connections, views, placement, writers and the two-server sequence, with failures injected per side and step.</summary>
internal sealed class TwoServerKit : IAsyncDisposable
{
    public const string App = "app";

    private readonly ConnectionMultiplexer _srcMux;
    private readonly ConnectionMultiplexer _dstMux;
#if NET9_0_OR_GREATER
    private readonly Lock _traceLock = new();
#else
    private readonly object _traceLock = new();
#endif
    private readonly string _id = Guid.NewGuid().ToString("N")[..10];
    private int _verifyReads;

    private TwoServerKit(ConnectionMultiplexer srcMux, ConnectionMultiplexer dstMux, KitOptions kit)
    {
        _srcMux = srcMux;
        _dstMux = dstMux;
        Kit = kit;
        LibraryPrefix = $"t{_id}:";
        SrcPrefix = $"s{_id}:";
        Group = "g" + _id;
        Options = new KeyMoveStateOptions { BarrierRetries = 2, BarrierWait = TimeSpan.FromSeconds(2), ExpectedReplicasPerMaster = kit.ExpectedReplicas };
        RawSrc = srcMux.GetDatabase().WithKeyPrefix(SrcPrefix);
        RawDst = dstMux.GetDatabase();
        SrcDb = Intercepted(RawSrc, RecordSide.Src);
        DstDb = Intercepted(RawDst, RecordSide.Dst);
        SrcCaps = CapsOf(srcMux, kit.SrcScripts);
        DstCaps = CapsOf(dstMux, kit.DstScripts);
        Views = MoveViews.Create(RawSrc, RawDst, SrcPrefix);
        Placement = new MovePlacement(Views, App, LibraryPrefix, SrcCaps, DstCaps, dstCluster: false);
        Conns = new SideConnections(SrcDb, DstDb, Options);
        Generations = new ScriptedGenerations(Conns.Generations());
        Tokens = new StateAcquisitionTokens(new MoveScripts(RawDst, true, DstCaps.Version), RawDst, g => Placement.State(g).Key);
        var state = Placement.State(Group).Key;
        DstState = Writer(DstDb, state, DstCaps, kit.DstReplicaFree, Lease);
        SrcWriter = Writer(SrcDb, Placement.Control(RecordSide.Src, Group)[0].Key, SrcCaps, kit.SrcReplicaFree, null);
        DstMaster = Writer(DstDb, Placement.Control(RecordSide.Dst, Group)[0].Key, DstCaps, kit.DstReplicaFree, null);
        Sequence = new InstanceTransitionSequence(Placement, Conns, DstState, SrcWriter, _ => DstMaster, Views, Generations, Options, (kind, detail) => Reports.Add((kind, detail)));
    }

    internal enum CutMode
    {
        Down,
        Lost,
    }

    public ManualTimeProvider Time { get; } = new(DateTimeOffset.UnixEpoch);

    public KitOptions Kit { get; }

    public ConnectionMultiplexer SrcMux => _srcMux;

    public ConnectionMultiplexer DstMux => _dstMux;

    public string LibraryPrefix { get; }

    public string SrcPrefix { get; }

    public string Group { get; }

    public KeyMoveStateOptions Options { get; }

    /// <summary>The Src prefixed view, as the application sees it, without the injected failures.</summary>
    public IDatabase RawSrc { get; }

    public IDatabase RawDst { get; }

    public IDatabase SrcDb { get; }

    public IDatabase DstDb { get; }

    public SideCaps SrcCaps { get; }

    public SideCaps DstCaps { get; }

    public MoveViews Views { get; }

    public MovePlacement Placement { get; }

    public SideConnections Conns { get; }

    /// <summary>The acquisition token as the state field of the Dst, for a claim that keeps its token across runs.</summary>
    public StateAcquisitionTokens Tokens { get; }

    /// <summary>The generation reader of the dedicated connections, with generation changes and unknown reads scripted per side.</summary>
    public ScriptedGenerations Generations { get; }

    public ServerRecordWriter DstState { get; }

    public ServerRecordWriter SrcWriter { get; }

    public ServerRecordWriter DstMaster { get; }

    public InstanceTransitionSequence Sequence { get; }

    public List<(string Kind, string Detail)> Reports { get; } = [];

    /// <summary>The EXEC of every transaction that carried a known script, in order, as <c>side:kind</c>.</summary>
    public List<string> Trace { get; } = [];

    public List<Cut> Cuts { get; } = [];

    /// <summary>Called with <c>side:kind</c> as each transaction with a known script is executed.</summary>
    public Action<string>? OnExec { get; set; }

    /// <summary>Called with the count of three-field record reads so far, before each one goes through.</summary>
    public Action<int>? OnVerify { get; set; }

    /// <summary>A failure of the unbound CLAIMPEND script call of the Src (it runs outside a transaction): Lost applies it and loses the reply, Down fails it before it is sent.</summary>
    public Cut? SrcPendCut { get; set; }

    /// <summary>Called before each whole-hash read on the Src (the claim read-back); may throw to make the read fail.</summary>
    public Action? BeforeSrcHashGetAll { get; set; }

    /// <summary>Called before each whole-hash read on the Dst (the claim read of the mirror), to lose a field between a write and its read.</summary>
    public Action? BeforeDstHashGetAll { get; set; }

    /// <summary>How many three-field record reads (the verification of barrier 1 and 2) went through the dedicated connections.</summary>
    public int VerifyReads => Volatile.Read(ref _verifyReads);

    public RedisKey StateKey => Placement.State(Group).Key;

    public RedisKey SrcRecord => Placement.Control(RecordSide.Src, Group)[0].Key;

    public RedisKey DstRecord => Placement.Control(RecordSide.Dst, Group)[0].Key;

    public static async Task<TwoServerKit> CreateAsync(TwoServerFixture fixture, KitOptions? options = null)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1, TWO_SERVER_SRC and TWO_SERVER_DST to run.");
        var kit = options ?? new KitOptions();
        var srcText = kit.UseReplicaSrc ? TwoServerFixture.SrcWithReplica : TwoServerFixture.Src;
        Assert.SkipUnless(srcText is not null, "Set MOVE_SIDES_MASTER to run.");
        var src = await ConnectionMultiplexer.ConnectAsync($"{srcText},allowAdmin=true,syncTimeout=10000,asyncTimeout=10000");
        var dst = await ConnectionMultiplexer.ConnectAsync($"{TwoServerFixture.Dst},allowAdmin=true,syncTimeout=10000,asyncTimeout=10000");
        return new TwoServerKit(src, dst, kit);
    }

    public static StateTarget Target(StateRecord from, string state = "S1", string direction = "fwd") =>
        new(state, direction, 0, from.Ep + 1, MoveScripts.NewNonce(), StateRecord.FormatPrev(from.State, from.Direction, from.NPop, from.Ep, from.Id), 2);

    /// <summary>The state at S0, <c>dur=1</c>, with one managed cache, created on the Dst.</summary>
    public async Task<StateRecord> SeedAsync()
    {
        var scripts = new MoveScripts(RawDst, true, DstCaps.Version);
        var caches = StateRecord.EncodeCaches(["c1"]);
        var init = await scripts.InitAsync(StateKey, caches, StateRecord.HashOfCaches(caches), 0, 1);
        init.Code.Should().Be(MoveScripts.StateCreated);
        return (await ReadStateAsync())!;
    }

    public async Task<StateRecord?> ReadStateAsync() => StateRecord.Parse(await RawDst.HashGetAllAsync(StateKey, CommandFlags.DemandMaster));

    public Task<TransitionResult> RunAsync(StateRecord from, StateTarget target, SequenceOptions? options = null) =>
        Sequence.RunAsync(Group, StateExpectation.Holding(from.Ep, from.Id, 1), target, options, CancellationToken.None);

    public async Task<Dictionary<string, string>> RecordAsync(RecordSide side)
    {
        var db = side == RecordSide.Src ? RawSrc : RawDst;
        var key = side == RecordSide.Src ? SrcRecord : DstRecord;
        return (await db.HashGetAllAsync(key, CommandFlags.DemandMaster)).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var mux in new[] { _srcMux, _dstMux })
        {
            foreach (var endpoint in mux.GetEndPoints())
            {
                var server = mux.GetServer(endpoint);
                if (server.IsReplica)
                {
                    continue;
                }

                await foreach (var key in server.KeysAsync(pattern: $"*{_id}*"))
                {
                    await mux.GetDatabase().KeyDeleteAsync(key);
                }
            }
        }

        await _srcMux.DisposeAsync();
        await _dstMux.DisposeAsync();
    }

    private static SideCaps CapsOf(ConnectionMultiplexer mux, bool scripts)
    {
        var endpoint = mux.GetEndPoints()[0];
        var master = new MasterCaps(endpoint, mux.GetServer(endpoint).Version, scripts, true, true, true, null, "noeviction");
        return new SideCaps([master], ScanMode.PerShard, false, [], []);
    }

    private static string KindOf(string script) => script switch
    {
        _ when script.Contains("ARGV[14]", StringComparison.Ordinal) => "TRANSIT",
        _ when script.Contains("'pst'", StringComparison.Ordinal) => "CTLSYNC",
        _ when script.Contains("'rel'", StringComparison.Ordinal) => "DURABLE",
        _ when script.Contains("'cmt'", StringComparison.Ordinal) => "CMT",
        _ when script.Contains("'dur', '1'", StringComparison.Ordinal) => "CTLCOMMIT",
        _ => "claim",
    };

    private static async Task<bool> LostReplyAsync(ITransaction real)
    {
        await real.ExecuteAsync(CommandFlags.DemandMaster | CommandFlags.NoRedirect);
        throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "the reply was lost", null, CommandStatus.Sent);
    }

    private static async Task<RedisResult> LostScriptAsync(IDatabase real, string script, RedisKey[] keys, RedisValue[] values, CommandFlags flags)
    {
        await real.ScriptEvaluateAsync(script, keys, values, flags);
        throw new RedisConnectionException(ConnectionFailureType.SocketFailure, CommandFlags.None, "the reply was lost", null, CommandStatus.Sent);
    }

    private ServerRecordWriter Writer(IDatabase db, RedisKey owner, SideCaps caps, bool replicaFree, Func<LeaseFence, Task<bool>>? holdsFence) =>
        new(
            new ServerBarrier(db, owner, Time, replicaFree) { Retries = Kit.BarrierRetries, Timeout = TimeSpan.FromSeconds(2) },
            caps,
            key => db.HashGetAllAsync(key, CommandFlags.DemandMaster),
            holdsFence);

    private async Task<bool> Lease(LeaseFence fence) => await RawDst.StringGetAsync(fence.Key, CommandFlags.DemandMaster) == fence.Token;

    // A database whose transactions pass through the cuts: a cut of mode Down makes the transaction's conditions fail (nothing applied); Lost applies it and loses the reply.
    private IDatabase Intercepted(IDatabase real, RecordSide side) =>
        InterceptingProxy.Wrap(real, (method, args) =>
        {
            ObserveRead(method.Name, args, side);
            if (method.Name == nameof(IDatabase.ScriptEvaluateAsync))
            {
                return InterceptPend(real, args, side);
            }

            return method.Name == nameof(IDatabase.CreateTransaction) ? Transaction(real.CreateTransaction(), side) : InterceptingProxy.Proceed;
        });

    private object InterceptPend(IDatabase real, object?[] args, RecordSide side)
    {
        if (args is not [string script, RedisKey[] keys, RedisValue[] values, CommandFlags flags]
            || script != ClaimScripts.Pend || SrcPendCut is not { } pend || pend.Side != side || !pend.Take())
        {
            return InterceptingProxy.Proceed;
        }

        return pend.Mode == CutMode.Lost
            ? LostScriptAsync(real, script, keys, values, flags)
            : Task.FromException<RedisResult>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "No connection is active/available to service this operation", null, CommandStatus.WaitingToBeSent));
    }

    private void ObserveRead(string method, object?[] args, RecordSide side)
    {
        if (method == nameof(IDatabase.HashGetAsync) && args.Length > 1 && args[1] is RedisValue[] { Length: 3 })
        {
            var reads = Interlocked.Increment(ref _verifyReads);
            OnVerify?.Invoke(reads);
        }

        if (method != nameof(IDatabase.HashGetAllAsync))
        {
            return;
        }

        var onRead = side == RecordSide.Src ? BeforeSrcHashGetAll : BeforeDstHashGetAll;
        onRead?.Invoke();
    }

    private ITransaction Transaction(ITransaction real, RecordSide side)
    {
        var kind = string.Empty;
        return InterceptingProxy.Wrap(real, (method, args) =>
        {
            if (method.Name == nameof(ITransaction.ScriptEvaluateAsync) && args[0] is string script && kind.Length == 0)
            {
                kind = KindOf(script);
                return InterceptingProxy.Proceed;
            }

            if (method.Name != nameof(ITransaction.ExecuteAsync) || args[0] is not CommandFlags || kind.Length == 0)
            {
                return InterceptingProxy.Proceed;
            }

            lock (_traceLock)
            {
                Trace.Add($"{side}:{kind}".ToLowerInvariant());
            }

            OnExec?.Invoke($"{side}:{kind}".ToLowerInvariant());

            var cut = Cuts.FirstOrDefault(c => c.Side == side && c.Kind == kind && c.Take());
            switch (cut?.Mode)
            {
                case CutMode.Down:
                    real.AddCondition(Condition.KeyExists("key-move-cut-never-exists"));
                    return InterceptingProxy.Proceed;
                case CutMode.Lost:
                    return LostReplyAsync(real);
                default:
                    return InterceptingProxy.Proceed;
            }
        });
    }

    /// <summary>The kinds are TRANSIT, CTLSYNC, CMT, CTLCOMMIT, DURABLE and claim; <paramref name="Times"/> of -1 cuts every one after the first <paramref name="skip"/> that pass.</summary>
    internal sealed class Cut(RecordSide side, string kind, CutMode mode, int times = -1, int skip = 0)
    {
#if NET9_0_OR_GREATER
        private readonly Lock _gate = new();
#else
        private readonly object _gate = new();
#endif
        private int _left = times;
        private int _skip = skip;

        public RecordSide Side { get; } = side;

        public string Kind { get; } = kind;

        public CutMode Mode { get; } = mode;

        public int Fired { get; private set; }

        public bool Take()
        {
            lock (_gate)
            {
                if (_left == 0)
                {
                    return false;
                }

                if (_skip > 0)
                {
                    _skip--;
                    return false;
                }

                if (_left > 0)
                {
                    _left--;
                }

                Fired++;
                return true;
            }
        }

        public void Lift()
        {
            lock (_gate)
            {
                _left = 0;
            }
        }
    }

    internal sealed record KitOptions(
        bool SrcScripts = true,
        bool DstScripts = true,
        bool SrcReplicaFree = true,
        bool DstReplicaFree = true,
        int BarrierRetries = 1,
        int? ExpectedReplicas = null,
        bool UseReplicaSrc = false);
}
