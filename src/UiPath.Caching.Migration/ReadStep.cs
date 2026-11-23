namespace UiPath.Caching.Migration;

/// <param name="Restart">The promotion fence moved, so the read retries from the destination replica.</param>
/// <param name="Rerun">A script answered that the identity changed: the unit starts again under the rules now in force.</param>
internal readonly record struct ReadStep<T>(PathRead<T> Read, bool Rerun = false, bool Restart = false)
    where T : class
{
    public static ReadStep<T> Again => new(default, true);

    public static ReadStep<T> Retry => new(default, false, true);

    public static ReadStep<T> Of(PathRead<T> read) => new(read);

    public static ReadStep<T> Of(ReadOutcome kind) => new(PathRead<T>.Of(kind));


}
