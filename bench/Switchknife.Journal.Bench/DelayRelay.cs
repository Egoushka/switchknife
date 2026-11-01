using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Switchknife.Journal.Bench;

/// <summary>
/// A TCP relay on loopback that holds every chunk for <c>oneWay</c> in each direction, so a connection through it has a
/// round trip of twice that. It stands in for a database across a network, which the bench cannot reach for real.
/// </summary>
internal sealed class DelayRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _oneWay;
    private Task _accepting = Task.CompletedTask;

    public DelayRelay(string host, int port, TimeSpan oneWay)
    {
        (_host, _port, _oneWay) = (host, port, oneWay);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _accepting = Task.Run(AcceptAsync);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            var upstream = new TcpClient { NoDelay = true };
            client.NoDelay = true;
            await upstream.ConnectAsync(_host, _port, _stop.Token).ConfigureAwait(false);
            _ = Pump(client.GetStream(), upstream.GetStream());
            _ = Pump(upstream.GetStream(), client.GetStream());
        }
    }

    private async Task Pump(Stream from, Stream to)
    {
        var queue = Channel.CreateUnbounded<(long Due, byte[] Data)>();
        var writer = Task.Run(async () =>
        {
            await foreach (var (due, data) in queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var wait = due - Environment.TickCount64;
                if (wait > 0)
                {
                    await Task.Delay((int)wait, _stop.Token).ConfigureAwait(false);
                }

                await to.WriteAsync(data, _stop.Token).ConfigureAwait(false);
            }
        });
        try
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await from.ReadAsync(buffer, _stop.Token).ConfigureAwait(false)) > 0)
            {
                await queue.Writer.WriteAsync((Environment.TickCount64 + (long)_oneWay.TotalMilliseconds, buffer[..read]), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            queue.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }
}
