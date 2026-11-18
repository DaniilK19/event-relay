using System.Net;
using System.Net.Sockets;

namespace EventRelay.Infrastructure.Webhooks;

/// <summary>Thrown from the connect callback; surfaces as the inner exception of an <see cref="HttpRequestException"/>.</summary>
public sealed class WebhookAddressNotAllowedException(string host, IPAddress address, AddressScope scope)
    : Exception($"Webhook host '{host}' resolves to {address} ({scope}); webhooks may not connect to it.")
{
    public string Host { get; } = host;

    public IPAddress Address { get; } = address;

    public AddressScope Scope { get; } = scope;
}

/// <summary>
/// <see cref="SocketsHttpHandler.ConnectCallback"/> for webhooks. The allowlist is checked on host
/// names; this closes the remaining gap at connection time. The name is resolved here, every
/// resolved address is checked, and the socket connects to exactly those addresses, so a DNS answer
/// cannot change between the check and the connection (DNS rebinding).
/// </summary>
public sealed class WebhookConnectionGuard(bool allowPrivateNetworks)
{
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await ResolveAsync(endpoint.Host, cancellationToken);

        // A host with any non-public address is rejected as a whole rather than silently
        // skipping that address: it is either misconfigured or an attempt to reach the inside.
        foreach (var address in addresses)
        {
            var scope = NetworkAddressRules.Classify(address);
            if (scope == AddressScope.Blocked || (scope == AddressScope.Private && !allowPrivateNetworks))
            {
                throw new WebhookAddressNotAllowedException(endpoint.Host, address, scope);
            }
        }

        var socket = Socket.OSSupportsIPv6
            ? new Socket(SocketType.Stream, ProtocolType.Tcp) // dual mode: IPv4 and IPv6
            : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;

        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var name = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (IPAddress.TryParse(name, out var literal))
        {
            return [literal];
        }

        var addresses = await Dns.GetHostAddressesAsync(name, cancellationToken);
        return addresses.Length > 0
            ? addresses
            : throw new SocketException((int)SocketError.HostNotFound);
    }
}
