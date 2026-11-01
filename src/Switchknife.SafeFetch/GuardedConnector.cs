using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Switchknife.SafeFetch;

/// <summary>Thrown from the connect callback when a host or port is not one a fetch may reach; the fetcher answers it as <see cref="FetchOutcome.Refused"/>.</summary>
public sealed class UnsafeAddressException(string host) : Exception($"{host} resolves to an address a fetch may not connect to");

/// <summary>
/// The connect callback of the fetcher's handler. It resolves the name once, refuses the whole fetch if any answer is not public,
/// and connects to the address it validated, so the handler never does a second lookup that could answer differently.
/// </summary>
public sealed class GuardedConnector(
    Func<string, CancellationToken, Task<IPAddress[]>> resolve, Func<IPAddress, int, CancellationToken, Task<Stream>> connect,
    IReadOnlyCollection<string>? trustedHosts = null)
{
    /// <summary>System DNS and a plain TCP socket.</summary>
    public static GuardedConnector Default { get; } = new(
        async (host, ct) => await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false),
        async (address, port, ct) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        });

    /// <summary>The same connector, except that the named hosts may be private and use any port. Give it only hosts the owner configured, never ones read from fetched content.</summary>
    public GuardedConnector Trusting(IReadOnlyCollection<string> hosts) => new(resolve, connect, hosts);

    /// <summary>The <see cref="SocketsHttpHandler.ConnectCallback"/>.</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct) =>
        await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct).ConfigureAwait(false);

    /// <summary>Resolves <paramref name="host"/> once and connects to the first checked address that answers.</summary>
    /// <exception cref="UnsafeAddressException">The port is not 80 or 443, or any answer is not public, for a host not trusted.</exception>
    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var trusted = trustedHosts?.Contains(host, StringComparer.OrdinalIgnoreCase) == true;
        if (!trusted && port is not (80 or 443))
        {
            throw new UnsafeAddressException($"{host}:{port}");
        }

        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await resolve(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || (!trusted && addresses.Any(a => !IpGuard.IsPublic(a))))
        {
            throw new UnsafeAddressException(host);
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            try
            {
                return await connect(address, port, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
