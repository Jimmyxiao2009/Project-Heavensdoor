using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace QQReborn.ServerHost;

/// <summary>Starts/stops RealServer (NapCat gateway) as a child process and captures stdout.</summary>
public sealed class ServerProcessManager : IDisposable
{
    private Process? _realServer;
    private readonly object _gate = new();

    public string RepoRoot { get; }
    public int RealServerPort { get; set; } = 8765;

    /// <summary>Always napcat for the product steward.</summary>
    public string Backend { get; set; } = "napcat";
    public string AccessPassword { get; set; }

    public string NapCatHttp { get; set; } = "http://127.0.0.1:3000";
    public string NapCatWs { get; set; } = "ws://127.0.0.1:3001";
    public string NapCatToken { get; set; } = "";

    public bool RealServerRunning
    {
        get { lock (_gate) return _realServer is { HasExited: false }; }
    }

    public event Action<string>? LogLine;

    public ServerProcessManager()
    {
        RepoRoot = FindRepoRoot();
        AccessPassword = LoadAccessPassword();
    }

    public async Task StartRealServerAsync()
    {
        if (RealServerRunning) { Append("RealServer already running."); return; }

        var exe = FindRealServerExe();
        ProcessStartInfo psi;
        if (exe != null)
        {
            psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            Append($"Starting RealServer: {exe}");
        }
        else
        {
            var proj = Path.Combine(RepoRoot, "server", "QQReborn.RealServer", "QQReborn.RealServer.csproj");
            if (!File.Exists(proj)) throw new FileNotFoundException("RealServer project not found: " + proj);
            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{proj}\" -c Release --no-launch-profile",
                WorkingDirectory = RepoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            Append($"Starting RealServer via: dotnet run ({proj})");
        }

        ApplyGatewayEnvironment(psi);
        Append("Backend = napcat (local gateway)");
        var p = StartCaptured(psi);
        lock (_gate) _realServer = p;

        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            if (!RealServerRunning) break;
            if (await ProbeAsync($"http://127.0.0.1:{RealServerPort}/"))
            {
                Append($"网关已启动 → http://0.0.0.0:{RealServerPort}  ws://127.0.0.1:{RealServerPort}/ws");
                return;
            }
        }
        Append("RealServer process started (probe still pending — check log).");
    }

    private void ApplyGatewayEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["QQREBORN_BACKEND"] = "napcat";
        psi.Environment["QQREBORN_MODE"] = "localGateway";
        psi.Environment["QQReborn__AccessPassword"] = AccessPassword ?? "";
        psi.Environment["QQREBORN_ACCESS_PASSWORD"] = AccessPassword ?? "";
        if (!string.IsNullOrWhiteSpace(NapCatHttp))
            psi.Environment["NAPCAT_HTTP"] = NapCatHttp.Trim();
        if (!string.IsNullOrWhiteSpace(NapCatWs))
            psi.Environment["NAPCAT_WS"] = NapCatWs.Trim();
        if (!string.IsNullOrWhiteSpace(NapCatToken))
            psi.Environment["NAPCAT_TOKEN"] = NapCatToken.Trim();
    }

    public async Task StartAllAsync()
    {
        SaveAccessPassword(AccessPassword);
        await CheckNapCatAsync();
        await StartRealServerAsync();
    }

    public async Task<bool> CheckNapCatAsync()
    {
        var url = NapCatHttp.TrimEnd('/') + "/get_login_info";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            if (!string.IsNullOrWhiteSpace(NapCatToken))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", NapCatToken);
            using var response = await http.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Append($"NapCat 检测失败: HTTP {(int)response.StatusCode} {body}");
                return false;
            }
            using var doc = JsonDocument.Parse(body);
            var userId = doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("user_id", out var uid) ? uid.ToString() : "未知账号";
            Append($"NapCat 在线: QQ {userId} ({NapCatHttp})");
            return true;
        }
        catch (Exception ex)
        {
            Append($"NapCat 未连接: {NapCatHttp}；请确认 NTQQ+NapCat 已登录并开启 OneBot HTTP API ({ex.Message})");
            return false;
        }
    }

    public void StopAll()
    {
        StopOne(ref _realServer, "RealServer");
    }

    private Process StartCaptured(ProcessStartInfo psi)
    {
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Append(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Append(e.Data); };
        p.Exited += (_, _) => Append($"[process exited pid={p.Id} code={p.ExitCode}]");
        if (!p.Start()) throw new InvalidOperationException("Failed to start process.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Append($"[started pid={p.Id}]");
        return p;
    }

    private void StopOne(ref Process? proc, string name)
    {
        Process? p;
        lock (_gate) { p = proc; proc = null; }
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                Append($"Stopping {name} (pid={p.Id})…");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch (Exception ex) { Append($"Stop {name}: {ex.Message}"); }
        finally
        {
            try { p.Dispose(); } catch { }
            Append($"{name} stopped.");
        }
    }

    private void Append(string line) => LogLine?.Invoke($"[{DateTime.Now:HH:mm:ss}] {line}");

    private static async Task<bool> ProbeAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var resp = await http.GetAsync(url);
            return resp.IsSuccessStatusCode || (int)resp.StatusCode < 500;
        }
        catch { return false; }
    }

    private string? FindRealServerExe()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "RealServer", "QQReborn.RealServer.exe"),
            Path.Combine(AppContext.BaseDirectory, "QQReborn.RealServer.exe"),
            Path.Combine(RepoRoot, "server", "QQReborn.RealServer", "bin", "Release", "net10.0", "QQReborn.RealServer.exe"),
            Path.Combine(RepoRoot, "out_server", "QQReborn.RealServer.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QQReborn.sln"))
                || Directory.Exists(Path.Combine(dir.FullName, "server", "QQReborn.RealServer")))
                return dir.FullName;
            dir = dir.Parent;
        }
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QQReborn.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    public void Dispose() => StopAll();

    private static string AccessPasswordPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QQReborn", "gateway.json");

    private static string LoadAccessPassword()
    {
        try
        {
            if (File.Exists(AccessPasswordPath))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(AccessPasswordPath));
                var value = doc.RootElement.GetProperty("accessPassword").GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch { }
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    }

    private static void SaveAccessPassword(string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AccessPasswordPath)!);
            File.WriteAllText(AccessPasswordPath, JsonSerializer.Serialize(new { accessPassword = value }));
        }
        catch { }
    }
}
