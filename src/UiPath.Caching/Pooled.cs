using System.Runtime.CompilerServices;

namespace UiPath.Caching;

/// <summary>
/// <see cref="PoolingAsyncValueTaskMethodBuilder{TResult}"/> under a short name: an async method marked
/// <c>[AsyncMethodBuilder(typeof(Pooled&lt;&gt;))]</c> reuses a pooled state machine box instead of allocating one per call.
/// </summary>
internal struct Pooled<TResult>
{
    private PoolingAsyncValueTaskMethodBuilder<TResult> _builder;

    public ValueTask<TResult> Task => _builder.Task;

    public static Pooled<TResult> Create() => new() { _builder = PoolingAsyncValueTaskMethodBuilder<TResult>.Create() };

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine =>
        _builder.Start(ref stateMachine);

    public void SetStateMachine(IAsyncStateMachine stateMachine) => _builder.SetStateMachine(stateMachine);

    public void SetResult(TResult result) => _builder.SetResult(result);

    public void SetException(Exception exception) => _builder.SetException(exception);

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        _builder.AwaitOnCompleted(ref awaiter, ref stateMachine);

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        _builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
}
