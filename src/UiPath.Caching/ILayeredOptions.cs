namespace UiPath.Caching;

/// <summary>What the layered entry flow needs of the options of an entry, whichever cache they are for.</summary>
internal interface ILayeredOptions<TSelf> : ICacheEntryOptions
    where TSelf : struct, ILayeredOptions<TSelf>
{
    CancellationToken Token { get; }

    TSelf WithToken(CancellationToken token);
}
