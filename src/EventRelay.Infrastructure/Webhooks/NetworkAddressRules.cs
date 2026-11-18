using System.Net;
using System.Net.Sockets;

namespace EventRelay.Infrastructure.Webhooks;

public enum AddressScope
{
    /// <summary>Routable on the internet: webhooks may connect to it.</summary>
    Public,

    /// <summary>
    /// Loopback and private networks (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7).
    /// Allowed only with <see cref="WebhookOptions.AllowPrivateNetworks"/>, e.g. for the local demo.
    /// </summary>
    Private,

    /// <summary>
    /// Never a legitimate webhook target, whatever the configuration: cloud metadata and platform
    /// endpoints (169.254.169.254, 168.63.129.16, fd00:ec2::254...), link-local, unspecified, multicast, reserved,
    /// documentation and tunnelling ranges.
    /// </summary>
    Blocked,
}

/// <summary>Classifies IP addresses for the connect-time SSRF check.</summary>
public static class NetworkAddressRules
{
    // Cloud metadata and platform endpoints outside the link-local range: most sit inside otherwise
    // "private" ranges, Azure's platform endpoint even in public address space. They hand out
    // credentials or machine configuration, so they are blocked even when private networks are
    // allowed. (The IPv4 endpoint 169.254.169.254 used by Azure, AWS and GCP is covered by the
    // link-local range below.)
    private static readonly IPAddress[] CloudMetadata =
    [
        IPAddress.Parse("100.100.100.200"), // Alibaba Cloud
        IPAddress.Parse("168.63.129.16"),   // Azure platform endpoint (WireServer, DNS, health probes)
        IPAddress.Parse("fd00:ec2::254"),   // AWS, IPv6
        IPAddress.Parse("fd20:ce::254"),    // Google Cloud, IPv6
    ];

    private static readonly IPNetwork[] PrivateV4 =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),   // carrier-grade NAT, shared address space
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
    ];

    private static readonly IPNetwork[] BlockedV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),  // link-local, cloud metadata services
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),     // multicast
        IPNetwork.Parse("240.0.0.0/4"),     // reserved, broadcast
    ];

    private static readonly IPNetwork[] PrivateV6 =
    [
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),        // unique local addresses
    ];

    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");

    // Everything outside global unicast (link-local, multicast, IPv4-compatible, site-local...) is blocked.
    private static readonly IPNetwork GlobalUnicastV6 = IPNetwork.Parse("2000::/3");

    private static readonly IPNetwork[] BlockedV6 =
    [
        IPNetwork.Parse("2001::/23"),       // IETF special purpose, including Teredo
        IPNetwork.Parse("2001:db8::/32"),   // documentation
        IPNetwork.Parse("2002::/16"),       // 6to4: embeds an arbitrary IPv4 address
        IPNetwork.Parse("3fff::/20"),       // documentation
    ];

    public static AddressScope Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            return Classify(address.MapToIPv4());
        }

        if (Array.Exists(CloudMetadata, metadata => metadata.Equals(address)))
        {
            return AddressScope.Blocked;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => ClassifyV4(address),
            AddressFamily.InterNetworkV6 => ClassifyV6(address),
            _ => AddressScope.Blocked,
        };
    }

    private static AddressScope ClassifyV4(IPAddress address)
    {
        if (Array.Exists(PrivateV4, network => network.Contains(address)))
        {
            return AddressScope.Private;
        }

        return Array.Exists(BlockedV4, network => network.Contains(address)) ? AddressScope.Blocked : AddressScope.Public;
    }

    private static AddressScope ClassifyV6(IPAddress address)
    {
        if (Array.Exists(PrivateV6, network => network.Contains(address)))
        {
            return AddressScope.Private;
        }

        // NAT64 (64:ff9b::a.b.c.d) reaches the embedded IPv4 address, so judge that one.
        if (Nat64.Contains(address))
        {
            return Classify(new IPAddress(address.GetAddressBytes().AsSpan(12, 4)));
        }

        return !GlobalUnicastV6.Contains(address) || Array.Exists(BlockedV6, network => network.Contains(address))
            ? AddressScope.Blocked
            : AddressScope.Public;
    }
}
