namespace UiPath.Caching.Migration;

/// <param name="Restart">The fence of a promotion moved: it starts again from the read of N.</param>
/// <param name="Rerun">A script answered that the identity changed: the unit starts again under the rules now in force.</param>
internal readonly record struct HashReadStep<T>(HashRead<T> Read, bool Rerun = false, bool Restart = false)
    where T : class
{
    public static HashReadStep<T> Again => new(default, true);

    public static HashReadStep<T> Retry => new(default, false, true);

    public static HashReadStep<T> Of(HashRead<T> read) => new(read);

    public static HashReadStep<T> Of(ReadOutcome kind) => new(HashRead<T>.Of(kind));
}
