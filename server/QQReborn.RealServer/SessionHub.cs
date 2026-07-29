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
            var session = conn.Session;
            session.Unsubscribe(conn);
            // There is no server-side resume token yet. Retaining an unbound NapCat
            // backend would leave its event WebSocket running forever and every client
            // reconnect would add another listener. The Shell restores its account on
            // the fresh connection, so dispose the now-idle backend promptly.
            if (session.IsEmpty && _sessions.TryRemove(session.Id, out var removed))
                _ = DisposeBackendAsync(removed.Backend);
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

    private static async Task DisposeBackendAsync(ISessionBackend backend)
    {
        if (backend is not IAsyncDisposable disposable) return;
        try { await disposable.DisposeAsync(); }
        catch (Exception ex) { Console.WriteLine("[Hub] backend dispose: " + ex.Message); }
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

    public bool IsEmpty
    {
        get { lock (_gate) return _subscribers.Count == 0; }
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
    private readonly object _sendGate = new();
    private readonly Queue<(string text, TaskCompletionSource<bool> completion)> _sendQueue = new();
    private bool _sendLoopRunning;

    public ClientConnection(string id, WebSocket socket, Func<string, Task> sendAsync)
    {
        Id = id;
        Socket = socket;
        _sendAsync = sendAsync;
    }

    public Task SendSafeAsync(string text)
    {
        // Broadcasts arrive from both the NapCat event loop and request handlers.
        // Starting one fire-and-forget send per frame lets SemaphoreSlim scheduling
        // reorder them, which is enough to make a fast burst look like a sync bug on
        // the Shell. Drain a bounded-in-lifetime queue instead of chaining tasks (a
        // long-running connection must not retain every previous frame).
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startLoop = false;
        lock (_sendGate)
        {
            _sendQueue.Enqueue((text, completion));
            if (!_sendLoopRunning)
            {
                _sendLoopRunning = true;
                startLoop = true;
            }
        }
        if (startLoop) _ = DrainSendQueueAsync();
        return completion.Task;
    }

    private async Task DrainSendQueueAsync()
    {
        while (true)
        {
            (string text, TaskCompletionSource<bool> completion) item;
            lock (_sendGate)
            {
                if (_sendQueue.Count == 0)
                {
                    _sendLoopRunning = false;
                    return;
                }
                item = _sendQueue.Dequeue();
            }

            try { await _sendAsync(item.text).ConfigureAwait(false); } catch { }
            item.completion.TrySetResult(true);
        }
    }
}
