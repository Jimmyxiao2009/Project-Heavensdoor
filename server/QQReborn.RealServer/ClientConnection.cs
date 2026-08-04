using System.Net.WebSockets;

namespace QQReborn.RealServer;

/// <summary>One WebSocket client of the UWP shell (ordered outbound send queue).</summary>
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
        // A FIFO drain keeps frame order stable under load.
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

            try { await _sendAsync(item.text).ConfigureAwait(false); } catch { /* socket may be closing */ }
            item.completion.TrySetResult(true);
        }
    }
}
