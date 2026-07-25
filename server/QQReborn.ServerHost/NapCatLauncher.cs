using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace QQReborn.ServerHost;

/// <summary>
/// Finds bundled NapCat Shell, stages a writable copy under LocalAppData,
/// ensures OneBot HTTP:3000 / WS:3001, and launches via NapCatWinBootMain.
/// </summary>
public sealed class NapCatLauncher
{
    public const int DefaultHttpPort = 3000;
    public const int DefaultWsPort = 3001;

    private readonly Action<string> _log;

    public NapCatLauncher(Action<string> log) => _log = log;

    /// <summary>Writable runtime root: %LocalAppData%\QQReborn\NapCat</summary>
    public static string RuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QQReborn", "NapCat");

    public string? FindShellSource()
    {
        foreach (var c in EnumerateShellCandidates())
        {
            if (IsShellDir(c)) return c;
        }
        return null;
    }

    public string? FindShellRuntime()
    {
        if (IsShellDir(RuntimeRoot)) return RuntimeRoot;
        var nested = Path.Combine(RuntimeRoot, "shell");
        if (IsShellDir(nested)) return nested;
        return null;
    }

    /// <summary>
    /// Copy install/bundled shell → LocalAppData (if needed) and force OneBot ports.
    /// Returns the shell directory ready to launch.
    /// </summary>
    public string EnsureRuntimeShell(string? preferredSource = null)
    {
        var source = preferredSource;
        if (string.IsNullOrWhiteSpace(source) || !IsShellDir(source))
            source = FindShellSource();
        if (string.IsNullOrWhiteSpace(source))
            throw new FileNotFoundException(
                "未找到 NapCat Shell。请把 NapCat.Shell 放到安装目录下的 NapCat\\，或设置环境变量 NAPCAT_SHELL。");

        var dest = RuntimeRoot;
        var marker = Path.Combine(dest, "NapCatWinBootMain.exe");
        var needCopy = !File.Exists(marker);

        if (!needCopy)
        {
            // Refresh if bundled source is newer than staged copy.
            try
            {
                var srcBoot = new FileInfo(Path.Combine(source, "NapCatWinBootMain.exe"));
                var dstBoot = new FileInfo(marker);
                if (srcBoot.Exists && srcBoot.LastWriteTimeUtc > dstBoot.LastWriteTimeUtc.AddSeconds(2))
                    needCopy = true;
            }
            catch { /* ignore */ }
        }

        if (needCopy)
        {
            _log($"同步 NapCat 到可写目录: {dest}");
            Directory.CreateDirectory(dest);
            CopyShell(source, dest);
        }
        else
        {
            _log($"使用已有 NapCat 运行目录: {dest}");
        }

        EnsureOneBotConfig(dest);
        return dest;
    }

    public void EnsureOneBotConfig(string shellDir)
    {
        var configDir = Path.Combine(shellDir, "config");
        Directory.CreateDirectory(configDir);

        var templateDir = FindTemplateDir();
        ApplyTemplate(templateDir, configDir, "onebot11.json");
        ApplyTemplate(templateDir, configDir, "napcat.json");

        // Force ports on every onebot11*.json (default + per-uin)
        foreach (var file in Directory.EnumerateFiles(configDir, "onebot11*.json"))
        {
            try
            {
                ForceOneBotPorts(file, DefaultHttpPort, DefaultWsPort, token: "");
            }
            catch (Exception ex)
            {
                _log($"写入 OneBot 配置失败 {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        _log($"OneBot 已配置: HTTP 127.0.0.1:{DefaultHttpPort}  WS 127.0.0.1:{DefaultWsPort}");
    }

    /// <summary>Start NapCat. Empty uin = QR login.</summary>
    public Process Start(string shellDir, string? quickLoginUin = null)
    {
        var boot = Path.Combine(shellDir, "NapCatWinBootMain.exe");
        var hook = Path.Combine(shellDir, "NapCatWinBootHook.dll");
        var main = Path.Combine(shellDir, "napcat.mjs");
        var loadJs = Path.Combine(shellDir, "loadNapCat.js");
        var patch = Path.Combine(shellDir, "qqnt.json");

        if (!File.Exists(boot)) throw new FileNotFoundException("missing NapCatWinBootMain.exe", boot);
        if (!File.Exists(hook)) throw new FileNotFoundException("missing NapCatWinBootHook.dll", hook);
        if (!File.Exists(main)) throw new FileNotFoundException("missing napcat.mjs", main);

        var qq = FindNtqqExe()
            ?? throw new FileNotFoundException(
                "未找到 NTQQ（QQ.exe）。请先安装官方 QQNT：C:\\Program Files\\Tencent\\QQNT\\QQ.exe");

        EnsureOneBotConfig(shellDir);

        // Same bootstrap as launcher-user.bat
        var mainUri = main.Replace('\\', '/');
        File.WriteAllText(loadJs, $"(async () => {{await import(\"file:///{mainUri}\")}})()", Encoding.UTF8);

        var psi = new ProcessStartInfo
        {
            FileName = boot,
            WorkingDirectory = shellDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(qq);
        psi.ArgumentList.Add(hook);
        if (!string.IsNullOrWhiteSpace(quickLoginUin))
            psi.ArgumentList.Add(quickLoginUin.Trim());

        psi.Environment["NAPCAT_PATCH_PACKAGE"] = patch;
        psi.Environment["NAPCAT_LOAD_PATH"] = loadJs;
        psi.Environment["NAPCAT_INJECT_PATH"] = hook;
        psi.Environment["NAPCAT_LAUNCHER_PATH"] = boot;
        psi.Environment["NAPCAT_MAIN_PATH"] = main;

        _log($"启动 NapCat: {boot}");
        _log($"  QQ = {qq}");
        if (!string.IsNullOrWhiteSpace(quickLoginUin))
            _log($"  快速登录 UIN = {quickLoginUin.Trim()}");
        else
            _log("  未指定 UIN → 使用扫码/已有会话登录");

        var p = Process.Start(psi)
            ?? throw new InvalidOperationException("NapCatWinBootMain 启动失败");
        _log($"[NapCat boot pid={p.Id}] 等待 OneBot :{DefaultHttpPort} …");
        return p;
    }

    public static string? FindNtqqExe()
    {
        var candidates = new List<string>
        {
            @"C:\Program Files\Tencent\QQNT\QQ.exe",
            @"C:\Program Files (x86)\Tencent\QQNT\QQ.exe",
        };

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\QQ");
            var uninstall = key?.GetValue("UninstallString") as string;
            if (!string.IsNullOrWhiteSpace(uninstall))
            {
                var dir = Path.GetDirectoryName(uninstall.Trim('"'));
                if (!string.IsNullOrWhiteSpace(dir))
                    candidates.Insert(0, Path.Combine(dir, "QQ.exe"));
            }
        }
        catch { /* ignore */ }

        return candidates.FirstOrDefault(File.Exists);
    }

    private IEnumerable<string> EnumerateShellCandidates()
    {
        // 1) Env override
        var env = Environment.GetEnvironmentVariable("NAPCAT_SHELL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env.Trim();
            yield return Path.Combine(env.Trim(), "shell");
        }

        // 2) Next to ServerHost (MSI / publish layout)
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "NapCat");
        yield return Path.Combine(baseDir, "NapCat", "shell");

        // 3) Already staged runtime
        yield return RuntimeRoot;
        yield return Path.Combine(RuntimeRoot, "shell");

        // 4) Common dev / legacy locations
        yield return @"D:\NapCat.Shell";
        yield return @"C:\NapCat.Shell";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "grok-goal-7c849b0de48f", "implementer", "NapCat", "shell");
    }

    private static bool IsShellDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, "NapCatWinBootMain.exe"))
               && File.Exists(Path.Combine(dir, "NapCatWinBootHook.dll"))
               && File.Exists(Path.Combine(dir, "napcat.mjs"));
    }

    private static string? FindTemplateDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var bundled = Path.Combine(baseDir, "NapCat", "config-templates");
        if (Directory.Exists(bundled)) return bundled;

        // Dev: repo installer templates
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            var t = Path.Combine(dir.FullName, "installer", "ServerHost", "napcat-config");
            if (Directory.Exists(t)) return t;
            dir = dir.Parent;
        }
        return null;
    }

    private static void ApplyTemplate(string? templateDir, string configDir, string fileName)
    {
        var dest = Path.Combine(configDir, fileName);
        if (File.Exists(dest)) return;
        if (templateDir == null) return;
        var src = Path.Combine(templateDir, fileName);
        if (File.Exists(src))
            File.Copy(src, dest, overwrite: false);
    }

    private static void ForceOneBotPorts(string path, int httpPort, int wsPort, string token)
    {
        JsonNode root;
        if (File.Exists(path))
        {
            root = JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var network = root["network"] as JsonObject ?? new JsonObject();
        root["network"] = network;

        network["httpServers"] = new JsonArray
        {
            new JsonObject
            {
                ["enable"] = true,
                ["enableWebsocket"] = true,
                ["enableCors"] = true,
                ["messagePostFormat"] = "array",
                ["name"] = "httpServer",
                ["port"] = httpPort,
                ["token"] = token,
                ["host"] = "127.0.0.1",
                ["debug"] = false,
            }
        };

        network["websocketServers"] = new JsonArray
        {
            new JsonObject
            {
                ["enable"] = true,
                ["enableForcePushEvent"] = true,
                ["heartInterval"] = 30000,
                ["reportSelfMessage"] = true,
                ["messagePostFormat"] = "array",
                ["name"] = "wsServer",
                ["port"] = wsPort,
                ["token"] = token,
                ["host"] = "127.0.0.1",
                ["debug"] = false,
            }
        };

        network["httpClients"] ??= new JsonArray();
        network["websocketClients"] ??= new JsonArray();
        network["httpSseServers"] ??= new JsonArray();
        network["plugins"] ??= new JsonArray();

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, root.ToJsonString(opts));
    }

    private static void CopyShell(string source, string dest)
    {
        // Skip runtime junk; keep node_modules / native / worker.
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cache", "logs", ".git"
        };

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            var top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (skipDirs.Contains(top)) continue;
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (skipDirs.Contains(top)) continue;
            if (rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;

            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        // Seed templates into config/
        var configDir = Path.Combine(dest, "config");
        Directory.CreateDirectory(configDir);
        var templates = FindTemplateDir();
        if (templates != null)
        {
            foreach (var name in new[] { "onebot11.json", "napcat.json" })
            {
                var src = Path.Combine(templates, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(configDir, name), overwrite: true);
            }
        }
    }
}
