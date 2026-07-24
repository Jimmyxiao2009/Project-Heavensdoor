using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace QQReborn.RealServer;

/// <summary>
/// Multi-tenant hub: each WebSocket client is bound to an <see cref="AccountSession"/>
/// that owns an isolated <see cref="ISessionBackend"/>. Events are delivered only to
/// connections subscribed to that session (no cross-account leakage).
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
            conn.Session.Unsubscribe(conn);
            // Keep the AccountSession alive for a while so a brief reconnect can re-bind;
            // true GC of idle sessions is a later pass.
        }
        Console.WriteLine($"[Hub] -conn {conn.Id[..8]}… total={_connections.Count} sessions={_sessions.Count}");
    }

    /// <summary>
    /// Ensure this connection has a backend session (create if needed).
    /// NapCat local gateway: one isolated session per Shell connection.
    /// </summary>
    public AccountSession EnsureSession(ClientConnection conn)
    {
        if (conn.Session != null) return conn.Session;

        var session = new AccountSession(
            Guid.NewGuid().ToString("N"),
            CreateBackend());
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

    private ISessionBackend CreateBackend()
    {
        // Each session gets its own NapCat-backed backend instance.
        return BackendFactory.Create(_config);
    }
}

/// <summary>One logged-in (or logging-in) QQ account on the server.</summary>
public sealed class AccountSession
{
    private readonly object _gate = new();
    private readonly List<ClientConnection> _subscribers = new();

    public string Id { get; }
    public ISessionBackend Backend { get; }
    public long? Uin { get; set; }

    public AccountSession(string id, ISessionBackend backend)
    {
        Id = id;
        Backend = backend;
        Backend.Broadcast += OnBackendBroadcast;
    }

    public void Subscribe(ClientConnection conn)
    {
        lock (_gate)
        {
            if (!_subscribers.Contains(conn))
                _subscribers.Add(conn);
        }
    }

    public void Unsubscribe(ClientConnection conn)
    {
        lock (_gate) _subscribers.Remove(conn);
    }

    private void OnBackendBroadcast(string frame)
    {
        ClientConnection[] snap;
        lock (_gate) snap = _subscribers.ToArray();
        foreach (var c in snap)
        {
            // Fire-and-forget per subscriber; send path has its own lock.
            _ = c.SendSafeAsync(frame);
        }
    }
}

/// <summary>One WebSocket client of the UWP shell.</summary>
public sealed class ClientConnection
{
    public string Id { get; }
    public WebSocket Socket { get; }
    public AccountSession? Session { get; set; }

    private readonly Func<string, Task> _sendAsync;

    public ClientConnection(string id, WebSocket socket, Func<string, Task> sendAsync)
    {
        Id = id;
        Socket = socket;
        _sendAsync = sendAsync;
    }

    public Task SendSafeAsync(string text)
    {
        try { return _sendAsync(text); }
        catch { return Task.CompletedTask; }
    }
}
