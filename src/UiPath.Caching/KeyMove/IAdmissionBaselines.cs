namespace UiPath.Caching.KeyMove;

// A failed read throws. Baseline replacements compare against the exact stored value.
internal interface IAdmissionBaselines
{
    Task<AdmissionBaseline?> ReadDstAsync(string app, CancellationToken cancellationToken);

    Task<bool> WriteDstAsync(string app, AdmissionBaseline baseline, AdmissionBaseline? replaces, CancellationToken cancellationToken);

    Task<AdmissionBaseline?> ReadSrcAsync(string app, CancellationToken cancellationToken);

    Task<bool> WriteSrcAsync(string app, AdmissionBaseline baseline, AdmissionBaseline? replaces, CancellationToken cancellationToken);

    Task ClearAsync(string app, string token, CancellationToken cancellationToken);
}
