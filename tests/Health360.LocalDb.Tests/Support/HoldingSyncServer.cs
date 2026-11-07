using System.Net;
using System.Net.Sockets;

namespace Health360.LocalDb.Tests.Support;

/// <summary>
/// A stand-in PowerSync endpoint that accepts every connection and never answers, so a sync client that dials it stays
/// connecting until it gives up. It counts the connections made and the ones the client still holds open: once sync is
/// stopped, the client has closed every one and dials no more. That is what "sync stopped" means on the wire, which
/// the SDK's status flags can lag behind. Connections are numbered in the order they arrive, so a test can tell the
/// first one apart from the rest.
/// </summary>
public sealed class HoldingSyncServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task accepting;
    private readonly List<bool> held = []; // by arrival; true while the client holds it open (guarded by itself)
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

    /// <summary>Waits until the client has made at least <paramref name="count"/> connections, or <paramref name="timeout"/> passes.</summary>
    public async Task<bool> AcceptedAtLeastAsync(int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Accepted < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, cancellationToken);
        }

        return Accepted >= count;
    }

    /// <summary>Connections from the <paramref name="first"/>th (0-based, by arrival) on that the client still holds open.</summary>
    public int OpenFrom(int first)
    {
        lock (held)
        {
            return held.Skip(first).Count(h => h);
        }
    }

    /// <summary>
    /// Waits until the client has closed every connection from the <paramref name="first"/>th (0-based, by arrival) on,
    /// or <paramref name="timeout"/> passes.
    /// </summary>
    public async Task<bool> AllClosedAsync(TimeSpan timeout, CancellationToken cancellationToken, int first = 0)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (OpenFrom(first) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, cancellationToken);
        }

        return OpenFrom(first) == 0;
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
            int index;
            lock (held)
            {
                index = held.Count;
                held.Add(true);
            }

            Interlocked.Increment(ref accepted);
            Interlocked.Increment(ref open);
            _ = HoldAsync(client, index);
        }
    }

    // Reads and ignores the request, never replying, until the client closes the connection.
    private async Task HoldAsync(TcpClient client, int index)
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

        lock (held)
        {
            held[index] = false;
        }

        Interlocked.Decrement(ref open);
    }
}
