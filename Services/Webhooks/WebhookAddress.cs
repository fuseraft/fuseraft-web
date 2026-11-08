using System.Net;
using System.Net.Sockets;

namespace fuseraft.Web.Services.Webhooks;

/// <summary>
/// Keeps webhooks from reaching this machine or its private networks (cloud metadata, admin ports, intranet
/// services) on a user's behalf. URLs are checked when saved, and every address is checked again as it's connected
/// to, so a name that later resolves somewhere private is caught too.
/// </summary>
public static class WebhookAddress
{
    /// <summary>Why <paramref name="url"/> can't be a webhook, or null when it can.</summary>
    public static string? Problem(string? url, WebhookOptions options)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "Enter a full http or https URL.";
        if (uri.UserInfo.Length > 0)
            return "Put credentials in the URL's path or query, not before the host.";
        var allowed = options.IsAllowedHost(uri.IdnHost);
        if (uri.Scheme != Uri.UriSchemeHttps && !allowed)
            return "Use https. An admin can allow plain http for a host in Fuseraft:Webhooks:AllowedHosts.";
        if (!allowed && (uri.IsLoopback || IPAddress.TryParse(uri.IdnHost, out var ip) && IsBlocked(ip)
                         || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))
            return "That address is on this machine or a private network. An admin can allow it in Fuseraft:Webhooks:AllowedHosts.";
        return null;
    }

    /// <summary>Loopback, private, link-local, shared and other addresses that aren't the public internet.</summary>
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 or 10 or 127 => true,
                100 => b[1] is >= 64 and <= 127,          // carrier-grade NAT
                169 => b[1] == 254,                       // link-local, cloud metadata
                172 => b[1] is >= 16 and <= 31,
                192 => b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2) || (b[1] == 88 && b[2] == 99),
                198 => b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100),
                203 => b[1] == 0 && b[2] == 113,
                _   => b[0] >= 224,                       // multicast, reserved, broadcast
            };
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return true;
        if (ip.Equals(IPAddress.IPv6None) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal)
            return true;
        var v6 = ip.GetAddressBytes();
        return (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8)  // documentation
            || (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b)  // NAT64, which reaches IPv4 addresses
            || (v6[0] == 0x20 && v6[1] == 0x02);                                   // 6to4, likewise
    }

    /// <summary>Connects to <paramref name="endpoint"/>'s first address that isn't blocked, unless its host is allowed.</summary>
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, WebhookOptions options, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        var allowed = options.IsAllowedHost(endpoint.Host);
        var usable = addresses.Where(a => allowed || !IsBlocked(a)).ToArray();
        if (usable.Length == 0)
            throw new HttpRequestException(addresses.Length == 0
                ? $"{endpoint.Host} doesn't resolve"
                : $"{endpoint.Host} is on this machine or a private network");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(usable, endpoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
