namespace Health360.Sync.Tests.Support;

/// <summary>
/// Airplane mode for a device's Storage requests: while <see cref="On"/>, every request fails as an unreachable
/// network does, before it leaves the process. The device's sync is cut separately (<see cref="Device.DisconnectAsync"/>).
/// </summary>
public sealed class Airplane : DelegatingHandler
{
    private volatile bool on;

    public Airplane()
        : base(new HttpClientHandler())
    {
    }

    public bool On
    {
        get => on;
        set => on = value;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        on
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Airplane mode."))
            : base.SendAsync(request, cancellationToken);
}
