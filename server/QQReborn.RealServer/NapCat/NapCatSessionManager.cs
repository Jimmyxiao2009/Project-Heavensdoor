using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQReborn.RealServer.NapCat;

/// <summary>
/// OneBot 11 / NapCat-backed session that speaks the App wire protocol.
/// Login is owned by NapCat/NTQQ; configureAccount only verifies connectivity
/// and starts the event stream.
/// Split into partials: Events, Read, Write, Admin, Helpers.
/// </summary>
public sealed partial class NapCatSessionManager : ISessionBackend, IAsyncDisposable
{
    private readonly NapCatOptions _opts;
    private readonly NapCatApiClient _api;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<JsonObject>> _messages = new();
    private readonly List<JsonObject> _conversations = new();
    private readonly List<JsonObject> _contacts = new();
    private sealed class ConvPref
    {
        public bool Pinned;
        public bool Muted;
        public string? LastReadAt;
        public int Unread;
    }

    private readonly Dictionary<string, ConvPref> _convPrefs = new();
    private readonly ConcurrentDictionary<string, JsonObject> _msgIndex = new();

    private long _selfUin;
    private string _selfNickname = "";
    private string _selfSignature = "";
    private bool _online;
    private CancellationTokenSource? _wsCts;
    private Task? _wsLoop;
    private readonly SemaphoreSlim _configureGate = new(1, 1);
    private Task? _populateTask;
    private long _populateUin;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _historyGates = new();
    private long _prefsUin;

    // Pending system requests (flag required by NapCat set_*_add_request).
    private readonly List<JsonObject> _friendRequests = new();
    private readonly List<JsonObject> _groupNotifications = new();
    private readonly Dictionary<string, string> _friendReqFlagByUin = new();
    private readonly Dictionary<string, string> _groupReqFlagByKey = new();
    private QzoneFeedClient? _qzone;

    public string BackendId => BackendFactory.NapCat;
    public event Action<string>? Broadcast;

    public NapCatSessionManager(NapCatOptions opts)
    {
        _opts = opts;
        _api = new NapCatApiClient(opts);
        Console.WriteLine($"[NapCat] HTTP={opts.HttpBase}  WS={opts.EventWs}");
    }

    public async ValueTask DisposeAsync()
    {
        _wsCts?.Cancel();
        if (_wsLoop != null)
        {
            try { await _wsLoop; } catch { /* ignore */ }
        }
        _api.Dispose();
    }

}
