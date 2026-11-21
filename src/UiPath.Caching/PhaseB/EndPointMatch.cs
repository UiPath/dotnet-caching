using System.Net;

namespace UiPath.Caching.PhaseB;

internal static class EndPointMatch
{
    /// <summary>Two endpoints name the same server: equal, or a host name that resolves to the other's address with the same port.</summary>
    public static bool Same(EndPoint? a, EndPoint? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (a.Equals(b))
        {
            return true;
        }

        return Port(a) == Port(b) && Addresses(a).Intersect(Addresses(b)).Any();
    }

    private static int Port(EndPoint endpoint) => endpoint switch
    {
        IPEndPoint ip => ip.Port,
        DnsEndPoint dns => dns.Port,
        _ => -1,
    };

    private static IPAddress[] Addresses(EndPoint endpoint)
    {
        switch (endpoint)
        {
            case IPEndPoint ip:
                return [ip.Address];
            case DnsEndPoint dns:
                try
                {
                    return IPAddress.TryParse(dns.Host, out var parsed) ? [parsed] : Dns.GetHostAddresses(dns.Host);
                }
                catch (System.Net.Sockets.SocketException)
                {
                    return [];
                }

            default:
                return [];
        }
    }
}
