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
    private Process? _napCatBoot;
    private readonly object _gate = new();
    private readonly NapCatLauncher _napCat;

    public string RepoRoot { get; }
    public int RealServerPort { get; set; } = 8765;

    /// <summary>Always napcat for the product steward.</summary>
    public string Backend { get; set; } = "napcat";
    public string AccessPassword { get; set; } = "";

    public string NapCatHttp { get; set; } = "http://127.0.0.1:3000";
    public string NapCatWs { get; set; } = "ws://127.0.0.1:3001";
    public string NapCatToken { get; set; } = "";
    /// <summary>Quick-login UIN for NapCat; empty = QR / existing session. Default = 大号.</summary>
    public string NapCatUin { get; set; } = AccountOption.Main.Uin;

    /// <summary>Account list for the steward picker (persisted).</summary>
    public List<AccountOption> Accounts { get; set; } = AccountOption.Defaults.ToList();

    public string NapCatUinDisplay =>
        string.IsNullOrWhiteSpace(NapCatUin)
            ? "扫码登录（不指定号）"
            : (Accounts.FirstOrDefault(a => a.Uin == NapCatUin)?.Display ?? NapCatUin);

    public bool RealServerRunning
    {
        get { lock (_gate) return _realServer is { HasExited: false }; }
    }

    public event Action<string>? LogLine;

    public ServerProcessManager()
    {
        RepoRoot = FindRepoRoot();
        _napCat = new NapCatLauncher(Append);
        LoadSettings();
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
        SaveSettings();
        if (!await CheckNapCatAsync())
        {
            Append("NapCat 未在线，尝试自动启动…");
            await StartNapCatAsync(waitForOnline: true);
        }
        await StartRealServerAsync();
    }

    /// <summary>
    /// Stage bundled NapCat (if needed), write OneBot 3000/3001 config, and launch.
    /// </summary>
    public async Task<bool> StartNapCatAsync(bool waitForOnline = true)
    {
        SaveSettings();
        if (await CheckNapCatAsync())
        {
            Append("NapCat 已在线，跳过启动。");
            return true;
        }

        string shell;
        try
        {
            shell = _napCat.EnsureRuntimeShell();
        }
        catch (Exception ex)
        {
            Append("无法准备 NapCat: " + ex.Message);
            throw;
        }

        try
        {
            var p = _napCat.Start(shell, string.IsNullOrWhiteSpace(NapCatUin) ? null : NapCatUin);
            lock (_gate) _napCatBoot = p;
        }
        catch (Exception ex)
        {
            Append("启动 NapCat 失败: " + ex.Message);
            throw;
        }

        if (!waitForOnline) return false;

        // Boot injects into QQ; OneBot may take a while (login / QR).
        for (var i = 0; i < 90; i++)
        {
            await Task.Delay(1000);
            if (await CheckNapCatAsync())
                return true;
            if (i is 15 or 30 or 60)
                Append($"仍在等待 NapCat OneBot… ({i}s) 若需扫码请在弹出的 QQ 窗口完成登录。");
        }

        Append("NapCat 在 90s 内未响应 OneBot HTTP；请确认 QQ 已登录后点「检测 NapCat」。");
        return false;
    }

    /// <summary>
    /// Stops the currently injected NTQQ/NapCat session and starts a fresh one
    /// using the selected UIN. This is intentionally separate from StopAll so
    /// stopping the gateway does not unexpectedly log the user out of QQ.
    /// </summary>
    public async Task<bool> RestartNapCatAsync(bool waitForOnline = true)
    {
        SaveSettings();
        StopExistingNapCatProcesses();

        // Let the old OneBot listener release port 3000 before launching the
        // new injected process. A short bounded wait keeps the UI responsive.
        for (var i = 0; i < 10; i++)
        {
            if (!await CheckNapCatAsync()) break;
            await Task.Delay(300);
        }

        return await StartNapCatAsync(waitForOnline);
    }

    public void EnsureNapCatConfigOnly()
    {
        try
        {
            var shell = _napCat.EnsureRuntimeShell();
            _napCat.EnsureOneBotConfig(shell);
            Append($"NapCat 配置目录: {Path.Combine(shell, "config")}");
        }
        catch (Exception ex)
        {
            Append("写入 NapCat 配置失败: " + ex.Message);
            throw;
        }
    }

    public string DescribeNapCatPaths()
    {
        var src = _napCat.FindShellSource();
        var runtime = _napCat.FindShellRuntime();
        var qq = NapCatLauncher.FindNtqqExe();
        return $"NapCat 源: {src ?? "(未找到)"}{Environment.NewLine}" +
               $"NapCat 运行: {runtime ?? NapCatLauncher.RuntimeRoot}{Environment.NewLine}" +
               $"NTQQ: {qq ?? "(未找到)"}";
    }

    public async Task<bool> CheckNapCatAsync()
    {
        var url = NapCatHttp.TrimEnd('/') + "/get_login_info";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            if (!string.IsNullOrWhiteSpace(NapCatToken))
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", NapCatToken);
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
            var nick = doc.RootElement.TryGetProperty("data", out var d2)
                && d2.TryGetProperty("nickname", out var nn) ? nn.GetString() : null;
            Append(string.IsNullOrWhiteSpace(nick)
                ? $"NapCat 在线: QQ {userId} ({NapCatHttp})"
                : $"NapCat 在线: QQ {userId} ({nick}) ({NapCatHttp})");
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
        // Do not kill NapCat/QQ by default — user may still need the session.
    }

    private void StopExistingNapCatProcesses()
    {
        var qqExe = NapCatLauncher.FindNtqqExe();
        var qqPath = qqExe == null ? null : Path.GetFullPath(qqExe);
        var stopped = 0;
        var qqProcesses = new List<Process>();

        foreach (var p in Process.GetProcessesByName("QQ"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (qqPath == null || path == null || !string.Equals(Path.GetFullPath(path), qqPath, StringComparison.OrdinalIgnoreCase))
                {
                    p.Dispose();
                    continue;
                }

                qqProcesses.Add(p);
            }
            catch
            {
                try { p.Dispose(); } catch { }
            }
        }

        // Kill the oldest matching process first. It is the NTQQ root; its child GPU/
        // utility processes then exit as part of the same process-tree operation. The old
        // implementation iterated children after the root and produced noisy
        // ReadProcessMemory errors for handles that had already become stale.
        foreach (var p in qqProcesses.OrderBy(GetProcessStartTime))
        {
            try
            {
                if (!p.HasExited)
                {
                    Append($"关闭旧 NTQQ/NapCat 会话 (pid={p.Id})…");
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    stopped++;
                }
            }
            catch (Exception ex)
            {
                Append($"关闭 NTQQ 进程失败 (pid={p.Id}): {ex.Message}");
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }

        foreach (var p in Process.GetProcessesByName("NapCatWinBootMain"))
        {
            try
            {
                if (!p.HasExited)
                {
                    Append($"关闭 NapCat 启动器 (pid={p.Id})…");
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    stopped++;
                }
            }
            catch (Exception ex)
            {
                Append($"关闭 NapCat 启动器失败 (pid={p.Id}): {ex.Message}");
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }

        Append(stopped == 0 ? "未找到可重启的 NTQQ/NapCat 进程。" : $"已关闭 {stopped} 个 NTQQ/NapCat 进程。 ");
    }

    private static DateTime GetProcessStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return DateTime.MaxValue; }
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

    public void Dispose()
    {
        StopAll();
        lock (_gate)
        {
            try { _napCatBoot?.Dispose(); } catch { }
            _napCatBoot = null;
        }
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QQReborn", "gateway.json");

    private void LoadSettings()
    {
        AccessPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        NapCatUin = AccountOption.Main.Uin;
        Accounts = AccountOption.Defaults.Select(a => new AccountOption { Uin = a.Uin, Label = a.Label }).ToList();
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("accessPassword", out var ap) && !string.IsNullOrWhiteSpace(ap.GetString()))
                AccessPassword = ap.GetString()!;
            if (root.TryGetProperty("napCatHttp", out var h) && !string.IsNullOrWhiteSpace(h.GetString()))
                NapCatHttp = h.GetString()!;
            if (root.TryGetProperty("napCatWs", out var w) && !string.IsNullOrWhiteSpace(w.GetString()))
                NapCatWs = w.GetString()!;
            if (root.TryGetProperty("napCatToken", out var t) && t.GetString() is { } tok)
                NapCatToken = tok;
            if (root.TryGetProperty("napCatUin", out var u) && u.ValueKind == JsonValueKind.String)
                NapCatUin = u.GetString() ?? AccountOption.Main.Uin;

            if (root.TryGetProperty("accounts", out var accArr) && accArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<AccountOption>();
                foreach (var el in accArr.EnumerateArray())
                {
                    var uin = el.TryGetProperty("uin", out var uu) ? uu.GetString() ?? "" : "";
                    var label = el.TryGetProperty("label", out var ll) ? ll.GetString() ?? "" : "";
                    if (list.Any(x => x.Uin == uin && x.Label == label)) continue;
                    list.Add(new AccountOption { Uin = uin, Label = label });
                }
                // Ensure defaults present
                foreach (var d in AccountOption.Defaults)
                {
                    if (!list.Any(x => x.Uin == d.Uin))
                        list.Insert(0, new AccountOption { Uin = d.Uin, Label = d.Label });
                }
                // Main first
                list = list
                    .OrderBy(a => a.Uin == AccountOption.Main.Uin ? 0 : string.IsNullOrEmpty(a.Uin) ? 2 : 1)
                    .ThenBy(a => a.Uin)
                    .ToList();
                if (list.Count > 0) Accounts = list;
            }

            // Migrate old default empty → main
            if (string.IsNullOrWhiteSpace(NapCatUin) && root.TryGetProperty("napCatUin", out _) == false)
                NapCatUin = AccountOption.Main.Uin;
        }
        catch { /* keep defaults */ }
    }

    public void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var payload = new
            {
                accessPassword = AccessPassword ?? "",
                napCatHttp = NapCatHttp ?? "http://127.0.0.1:3000",
                napCatWs = NapCatWs ?? "ws://127.0.0.1:3001",
                napCatToken = NapCatToken ?? "",
                napCatUin = NapCatUin ?? AccountOption.Main.Uin,
                accounts = Accounts.Select(a => new { uin = a.Uin, label = a.Label }).ToArray(),
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
    }
}
