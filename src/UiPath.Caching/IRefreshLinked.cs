namespace UiPath.Caching;

/// <summary>An entry a peer refresh links to the copy it keeps in its place by the refresh's number, not by reference: a late event on the replaced entry's token finds the copy, and neither entry keeps the other alive.</summary>
internal interface IRefreshLinked
{
    const long None = 0;

    /// <summary>The number of the refresh that replaced this entry; <see cref="None"/> until one has.</summary>
    long ReplacedBy { get; set; }

    /// <summary>The number of the refresh that kept this entry in place of another; <see cref="None"/> when a local write or read stored it.</summary>
    long Replaces { get; set; }
}
