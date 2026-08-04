using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace QQReborn.RealServer;

/// <summary>
/// Connection registry: each Shell WebSocket binds to an isolated
/// <see cref="AccountSession"/> (one NapCat-backed <see cref="ISessionBackend"/>).
/// Pushes stay on the owning session — no cross-account broadcast.
/// </summary>
public sealed class SessionHub
{
    private readonly IConfiguration _config;
    private readonly ConcurrentDictionary<string, AccountSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();

    public SessionHub(IConfiguration config)
    {
        _config = config;
    }

    public string DefaultBackendId => BackendFactory.ResolveBackendId(_config);

    public ClientConnection RegisterConnection(WebSocket socket, Func<string, Task> sendAsync)
    {
        var conn = new ClientConnection(Guid.NewGuid().ToString("N"), socket, sendAsync);
        _connections[conn.Id] = conn;
        Console.WriteLine($"[Hub] +conn {conn.Id[..8]}… total={_connections.Count}");
        return conn;
    }

    public void UnregisterConnection(ClientConnection conn)
    {
        if (conn == null) return;
        _connections.TryRemove(conn.Id, out _);
        if (conn.Session != null)
        {
            var session = conn.Session;
            session.Unsubscribe(conn);
            // No server-side resume token: dispose idle NapCat backends so reconnects
            // do not stack event listeners. Shell rebinds via configureAccount.
            if (session.IsEmpty && _sessions.TryRemove(session.Id, out var removed))
                _ = DisposeBackendAsync(removed.Backend);
        }
        Console.WriteLine($"[Hub] -conn {conn.Id[..8]}… total={_connections.Count} sessions={_sessions.Count}");
    }

    /// <summary>Create a backend session for this connection if missing.</summary>
    public AccountSession EnsureSession(ClientConnection conn)
    {
        if (conn.Session != null) return conn.Session;

        var session = new AccountSession(
            Guid.NewGuid().ToString("N"),
            BackendFactory.Create(_config));
        _sessions[session.Id] = session;
        Bind(conn, session);
        Console.WriteLine($"[Hub] new session {session.Id[..8]}… backend={session.Backend.BackendId}");
        return session;
    }

    public void Bind(ClientConnection conn, AccountSession session)
    {
        if (conn.Session != null && !ReferenceEquals(conn.Session, session))
            conn.Session.Unsubscribe(conn);
        conn.Session = session;
        session.Subscribe(conn);
    }

    public ISessionBackend BackendFor(ClientConnection conn)
        => EnsureSession(conn).Backend;

    private static async Task DisposeBackendAsync(ISessionBackend backend)
    {
        if (backend is not IAsyncDisposable disposable) return;
        try { await disposable.DisposeAsync(); }
        catch (Exception ex) { Console.WriteLine("[Hub] backend dispose: " + ex.Message); }
    }
}
