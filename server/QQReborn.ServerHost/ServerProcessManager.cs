using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace QQReborn.ServerHost;

/// <summary>Starts/stops RealServer and LocalSignProxy as child processes and captures stdout.</summary>
public sealed class ServerProcessManager : IDisposable
{
    private Process? _realServer;
    private Process? _signProxy;
    private readonly object _gate = new();

    public string RepoRoot { get; }
    public int RealServerPort { get; set; } = 8765;
    public int SignProxyPort { get; set; } = 18488;

    /// <summary>lagrange | napcat — injected as QQREBORN_BACKEND when starting RealServer.</summary>
    public string Backend { get; set; } = "lagrange";

    public string NapCatHttp { get; set; } = "http://127.0.0.1:3000";
    public string NapCatWs { get; set; } = "ws://127.0.0.1:3001";
    public string NapCatToken { get; set; } = "";

    public bool RealServerRunning
    {
        get { lock (_gate) return _realServer is { HasExited: false }; }
    }

    public bool SignProxyRunning
    {
        get { lock (_gate) return _signProxy is { HasExited: false }; }
    }

    public event Action<string>? LogLine;

    public ServerProcessManager()
    {
        RepoRoot = FindRepoRoot();
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

        ApplyBackendEnvironment(psi);
        Append($"Backend = {Backend}");
        var p = StartCaptured(psi);
        lock (_gate) _realServer = p;

        // Wait briefly for listen
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            if (!RealServerRunning) break;
            if (await ProbeAsync($"http://127.0.0.1:{RealServerPort}/"))
            {
                Append($"RealServer is up → http://0.0.0.0:{RealServerPort}  ws://127.0.0.1:{RealServerPort}/ws");
                return;
            }
        }
        Append("RealServer process started (probe still pending — check log).");
    }

    private void ApplyBackendEnvironment(ProcessStartInfo psi)
    {
        var backend = string.IsNullOrWhiteSpace(Backend) ? "lagrange" : Backend.Trim().ToLowerInvariant();
        psi.Environment["QQREBORN_BACKEND"] = backend;
        if (backend is "napcat" or "onebot" or "ob11")
        {
            if (!string.IsNullOrWhiteSpace(NapCatHttp))
                psi.Environment["NAPCAT_HTTP"] = NapCatHttp.Trim();
            if (!string.IsNullOrWhiteSpace(NapCatWs))
                psi.Environment["NAPCAT_WS"] = NapCatWs.Trim();
            if (!string.IsNullOrWhiteSpace(NapCatToken))
                psi.Environment["NAPCAT_TOKEN"] = NapCatToken.Trim();
        }
    }

    public async Task StartSignProxyAsync()
    {
        // NapCat does not need our LocalSignProxy; skip if user only wants NapCat stack.
        if (string.Equals(Backend, "napcat", StringComparison.OrdinalIgnoreCase))
        {
            Append("Skip LocalSignProxy (NapCat backend — NTQQ owns signing).");
            return;
        }
        if (SignProxyRunning) { Append("LocalSignProxy already running."); return; }

        var exe = FindSignProxyExe();
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
            Append($"Starting LocalSignProxy: {exe}");
        }
        else
        {
            var proj = Path.Combine(RepoRoot, "tools", "LocalSignProxy", "LocalSignProxy.csproj");
            if (!File.Exists(proj))
            {
                Append("LocalSignProxy project not found — skip.");
                return;
            }
            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{proj}\" -c Release --no-build",
                WorkingDirectory = RepoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // Build first if needed
            try
            {
                Append("Building LocalSignProxy…");
                var build = Process.Start(new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{proj}\" -c Release -v q",
                    WorkingDirectory = RepoRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (build != null)
                {
                    await build.WaitForExitAsync();
                    Append(build.ExitCode == 0 ? "LocalSignProxy build OK." : "LocalSignProxy build failed.");
                }
            }
            catch (Exception ex) { Append("Build error: " + ex.Message); }

            Append($"Starting LocalSignProxy via: dotnet run");
        }

        var p = StartCaptured(psi);
        lock (_gate) _signProxy = p;

        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(200);
            if (await ProbeAsync($"http://127.0.0.1:{SignProxyPort}/health"))
            {
                Append($"LocalSignProxy is up → http://127.0.0.1:{SignProxyPort}");
                return;
            }
        }
        Append("LocalSignProxy process started (probe still pending).");
    }

    public async Task StartAllAsync()
    {
        await StartSignProxyAsync();
        await StartRealServerAsync();
    }

    public void StopAll()
    {
        StopOne(ref _signProxy, "LocalSignProxy");
        StopOne(ref _realServer, "RealServer");
    }

    public async Task<string> TestSpaceWebhookAsync(string? author = null, string? text = null)
    {
        var url = $"http://127.0.0.1:{RealServerPort}/webhook/space";
        var payload = new
        {
            author = author ?? "测试用户",
            text = text ?? ("面板自测动态 " + DateTime.Now.ToString("HH:mm:ss")),
            time = DateTimeOffset.Now.ToString("o"),
            images = Array.Empty<string>(),
        };
        var json = JsonSerializer.Serialize(payload);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var resp = await http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            Append($"Webhook POST {url} → {(int)resp.StatusCode} {body}");
            return body;
        }
        catch (Exception ex)
        {
            Append("Webhook test failed: " + ex.Message);
            return ex.Message;
        }
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

    private string? FindSignProxyExe()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "LocalSignProxy", "LocalSignProxy.exe"),
            Path.Combine(AppContext.BaseDirectory, "LocalSignProxy.exe"),
            Path.Combine(RepoRoot, "tools", "LocalSignProxy", "bin", "Release", "net10.0", "LocalSignProxy.exe"),
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
        // Fall back: walk up from cwd
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
}
