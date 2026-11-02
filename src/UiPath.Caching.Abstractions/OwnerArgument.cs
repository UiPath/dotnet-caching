namespace UiPath.Caching;

/// <summary>The state of an owner and argument call; <see cref="Invoke"/> is one static delegate per instantiation, so a hit allocates nothing.</summary>
[SuppressMessage("Major Code Smell", "S2436:Types and methods should not have too many generic parameters", Justification = "The owner, the argument and the result type are all part of the state.")]
internal readonly struct OwnerArgument<TOwner, TArg, TResult>(TOwner owner, TArg argument, Func<TOwner, TArg, CancellationToken, TResult> generator)
{
    internal static readonly Func<OwnerArgument<TOwner, TArg, TResult>, CancellationToken, TResult> Invoke =
        static (state, token) => state.Generator(state.Owner, state.Argument, token);

    public TOwner Owner { get; } = owner;

    public TArg Argument { get; } = argument;

    public Func<TOwner, TArg, CancellationToken, TResult> Generator { get; } = generator;
}
