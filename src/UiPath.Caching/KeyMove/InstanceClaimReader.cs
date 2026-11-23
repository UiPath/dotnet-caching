using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed class InstanceClaimReader(ClaimStore src, ClaimStore dstMirror)
{
    private const string AuthorityKind = "im:";
    private const string MirrorKind = "imd:";

    public async Task<InstanceClaims> ReadAsync(string group, CancellationToken cancellationToken)
    {
        var authority = await src.ReadAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mirror = await dstMirror.ReadAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return new InstanceClaims(authority.Moves.GetValueOrDefault(AuthorityKind + group), mirror.Moves.GetValueOrDefault(MirrorKind + group), authority.Malformed, true, mirror.Malformed);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return new InstanceClaims(authority.Moves.GetValueOrDefault(AuthorityKind + group), null, authority.Malformed, false, false);
        }
    }

    public async Task<ClaimFields?> FieldsAsync(string group, CancellationToken cancellationToken)
    {
        var claims = await ReadAsync(group, cancellationToken).ConfigureAwait(false);
        if (claims.SrcMalformed)
        {
            return null;
        }

        if (claims.Src is { } authority)
        {
            var release = authority.Cert.Length > 0 ? $"{authority.Token}:{authority.Cert}" : null;
            if (authority.IsLive)
            {
                return new ClaimFields(authority.Descriptor, authority.Token, release);
            }

            return release is null ? null : new ClaimFields(null, null, release);
        }

        return claims.Mirror is { IsLive: true } mirror && !claims.MirrorMalformed ? new ClaimFields(mirror.Descriptor, mirror.Token, null) : null;
    }
}
