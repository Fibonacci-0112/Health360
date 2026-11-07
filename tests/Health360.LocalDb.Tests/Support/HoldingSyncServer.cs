using System.Net;
using System.Net.Sockets;

namespace Health360.LocalDb.Tests.Support;

/// <summary>
/// A stand-in PowerSync endpoint that accepts every connection and never answers, so a sync client that dials it stays
/// connecting until it gives up. It counts the connections made and the ones the client still holds open: once sync is
/// stopped, the client has closed every one and dials no more. That is what "sync stopped" means on the wire, which
/// the SDK's status flags can lag behind.
/// </summary>
public sealed class HoldingSyncServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task accepting;
    private int accepted;
    private int open;

    public HoldingSyncServer()
    {
        listener.Start();
        Url = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        accepting = Task.Run(AcceptAsync);
    }

    public Uri Url { get; }

    /// <summary>Connections the client has made so far.</summary>
    public int Accepted => Volatile.Read(ref accepted);

    /// <summary>Connections the client still holds open.</summary>
    public int Open => Volatile.Read(ref open);

    /// <summary>Waits until the client has closed every connection, or <paramref name="timeout"/> passes.</summary>
    public async Task<bool> AllClosedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Open > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, cancellationToken);
        }

        return Open == 0;
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        try
        {
            await accepting;
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stop.Token);
            Interlocked.Increment(ref accepted);
            Interlocked.Increment(ref open);
            _ = HoldAsync(client);
        }
    }

    // Reads and ignores the request, never replying, until the client closes the connection.
    private async Task HoldAsync(TcpClient client)
    {
        using (client)
        {
            var buffer = new byte[4096];
            try
            {
                var stream = client.GetStream();
                while (await stream.ReadAsync(buffer, stop.Token) > 0)
                {
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        Interlocked.Decrement(ref open);
    }
}
