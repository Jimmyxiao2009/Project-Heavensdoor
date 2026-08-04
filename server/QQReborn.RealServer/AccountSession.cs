namespace QQReborn.RealServer;

/// <summary>
/// One NapCat-backed account context on the gateway.
/// Subscribers receive <see cref="ISessionBackend.Broadcast"/> frames (messages, login, …).
/// </summary>
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
            _ = c.SendSafeAsync(frame);
    }
}
