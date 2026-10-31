namespace Health360.Sync.Tests.Support;

/// <summary>
/// Two users and three installs, shared by the tests in a class: Alex on <see cref="A1"/> and
/// <see cref="A2"/>, Blake on <see cref="B"/>, all connected. Does nothing unless <c>SYNC_E2E=1</c>.
/// </summary>
public sealed class SyncFixture : IAsyncLifetime
{
    private SyncBackend? backend;

    public SyncBackend Backend => backend!;

    public TestUser Alex { get; private set; } = null!;

    public TestUser Blake { get; private set; } = null!;

    public Device A1 { get; private set; } = null!;

    public Device A2 { get; private set; } = null!;

    public Device B { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!SyncBackend.Enabled)
        {
            return;
        }

        backend = SyncBackend.FromEnvironment();
        Alex = await backend.CreateUserAsync("alex", CancellationToken.None);
        Blake = await backend.CreateUserAsync("blake", CancellationToken.None);
        A1 = Device.Create(backend, Alex);
        A2 = Device.Create(backend, Alex);
        B = Device.Create(backend, Blake);
        await Task.WhenAll(A1.ConnectAsync(), A2.ConnectAsync(), B.ConnectAsync());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var device in new[] { A1, A2, B })
        {
            if (device is not null)
            {
                await device.DisposeAsync();
            }
        }

        if (backend is not null)
        {
            await backend.DisposeAsync();
        }
    }
}
