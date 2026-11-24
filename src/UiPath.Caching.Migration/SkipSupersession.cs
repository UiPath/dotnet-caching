namespace UiPath.Caching.Migration;

/// <summary>The default handler: the key is left alone and the next pass tries it again.</summary>
internal sealed class SkipSupersession : ISupersessionHandler
{
    public static SkipSupersession Instance { get; } = new();

    public Task<SupersessionAction> OnSupersededAsync(SupersededKey context, CancellationToken cancellationToken) => Task.FromResult(SupersessionAction.Skip);
}
