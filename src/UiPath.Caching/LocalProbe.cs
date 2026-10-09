namespace UiPath.Caching;

/// <summary>What one read of the local tier established: <see cref="Miss"/> means it holds nothing under the key, <see cref="Unavailable"/> that the read could not be judged here and the key path must make it.</summary>
internal enum LocalProbe
{
    Unavailable,
    Miss,
    Hit,
}
