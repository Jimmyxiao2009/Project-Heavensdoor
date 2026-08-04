using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace QQReborn.ServerHost;

/// <summary>
/// Downloads NapCat.Shell from GitHub (with China mirrors) into a local cache,
/// then stages it for <see cref="NapCatLauncher"/>.
/// </summary>
public sealed class NapCatInstaller
{
    public const string GitHubLatestApi = "https://api.github.com/repos/NapNeko/NapCatQQ/releases/latest";
    public const string QqntDownloadPage = "https://im.qq.com/download/";
    public const string NapCatReleasesPage = "https://github.com/NapNeko/NapCatQQ/releases/latest";

    /// <summary>Preferred asset names (first match wins).</summary>
    private static readonly string[] PreferredAssets =
    {
        "NapCat.Shell.zip",
        "NapCat.Shell.Windows.zip",
        "NapCat.Shell.Windows.OneKey.zip",
    };

    private static readonly string[] UrlMirrors =
    {
        // identity first
        "{0}",
        // common China-friendly GitHub proxies (best-effort)
        "https://ghproxy.net/{0}",
        "https://mirror.ghproxy.com/{0}",
        "https://gitdl.cn/{0}",
        "https://gh.ddlc.top/{0}",
    };

    private readonly Action<string> _log;
    private readonly HttpClient _http;

    public NapCatInstaller(Action<string> log)
    {
        _log = log;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QQReborn-ServerHost/0.2 (+https://github.com/)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static string DownloadCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QQReborn", "cache");

    public static string InstallStagingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QQReborn", "NapCat-download");

    /// <summary>
    /// Ensure a valid NapCat shell source exists. Returns the shell directory path.
    /// Downloads from GitHub when missing (or always when <paramref name="forceUpdate"/>).
    /// </summary>
    public async Task<string> EnsureInstalledAsync(bool forceUpdate = false, CancellationToken ct = default)
    {
        if (!forceUpdate)
        {
            // Already staged runtime is fine as source
            if (NapCatLauncher.IsShellDirectory(NapCatLauncher.RuntimeRoot))
            {
                _log("已检测到本机 NapCat 运行目录。");
                return NapCatLauncher.RuntimeRoot;
            }

            // Previous download staging
            if (NapCatLauncher.IsShellDirectory(InstallStagingDir))
            {
                _log("使用已下载的 NapCat 安装包目录。");
                return InstallStagingDir;
            }
        }
        else
        {
            _log("强制更新：重新下载最新 NapCat…");
        }

        _log("开始从 GitHub 获取 NapCat Shell…");
        var zipPath = await DownloadLatestShellZipAsync(ct);
        var shell = ExtractShell(zipPath);
        _log($"NapCat 安装完成: {shell}");
        return shell;
    }

    public async Task<string> DownloadLatestShellZipAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(DownloadCacheDir);
        var (version, assetName, downloadUrl) = await ResolveLatestAssetAsync(ct);
        _log($"最新版本: {version}  资源: {assetName}");

        var destZip = Path.Combine(DownloadCacheDir, $"{version}_{assetName}");
        if (File.Exists(destZip) && new FileInfo(destZip).Length > 1_000_000)
        {
            _log($"复用本地缓存: {destZip}");
            return destZip;
        }

        var tmp = destZip + ".partial";
        if (File.Exists(tmp)) try { File.Delete(tmp); } catch { /* ignore */ }

        Exception? last = null;
        foreach (var pattern in UrlMirrors)
        {
            var url = string.Format(pattern, downloadUrl);
            try
            {
                _log($"下载: {url}");
                await DownloadFileWithProgressAsync(url, tmp, ct);
                if (new FileInfo(tmp).Length < 100_000)
                    throw new InvalidOperationException("下载文件过小，可能不是有效安装包。");
                if (File.Exists(destZip)) File.Delete(destZip);
                File.Move(tmp, destZip);
                _log($"下载完成 ({FormatSize(new FileInfo(destZip).Length)})");
                return destZip;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                _log($"下载失败: {ex.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException(
            "无法自动下载 NapCat。请检查网络后重试，或手动从 GitHub 下载 NapCat.Shell.zip。\n" +
            NapCatReleasesPage +
            (last != null ? "\n原因: " + last.Message : ""));
    }

    public string ExtractShell(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("安装包不存在", zipPath);

        var extractRoot = Path.Combine(DownloadCacheDir, "extract_" + Guid.NewGuid().ToString("N")[..8]);
        if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true);
        Directory.CreateDirectory(extractRoot);

        _log("解压 NapCat…");
        ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true);

        var shell = FindShellInTree(extractRoot)
            ?? throw new InvalidOperationException(
                "压缩包内未找到 NapCatWinBootMain.exe / napcat.mjs。请确认下载的是 NapCat.Shell.zip。");

        // Copy into stable staging so next boot does not re-extract
        var dest = InstallStagingDir;
        if (Directory.Exists(dest))
        {
            try { Directory.Delete(dest, true); }
            catch
            {
                // busy files — stage beside with version stamp
                dest = InstallStagingDir + "-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            }
        }

        _log($"部署到: {dest}");
        CopyDirectory(shell, dest);

        try { Directory.Delete(extractRoot, true); } catch { /* ignore */ }

        if (!NapCatLauncher.IsShellDirectory(dest))
            throw new InvalidOperationException("部署后目录校验失败: " + dest);

        return dest;
    }

    private async Task<(string Version, string AssetName, string Url)> ResolveLatestAssetAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (var pattern in UrlMirrors)
        {
            var apiUrl = string.Format(pattern, GitHubLatestApi);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.UserAgent.ParseAdd("QQReborn-ServerHost/0.2");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    last = new HttpRequestException($"HTTP {(int)resp.StatusCode} from {apiUrl}");
                    continue;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;
                var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "latest" : "latest";
                if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Release 无 assets 列表");

                JsonElement? chosen = null;
                string? chosenName = null;
                foreach (var preferred in PreferredAssets)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (name != null && name.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                        {
                            chosen = a;
                            chosenName = name;
                            break;
                        }
                    }
                    if (chosen != null) break;
                }

                // Fallback: any asset containing "Shell" and ending with .zip
                if (chosen == null)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (name != null
                            && name.Contains("Shell", StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("linux", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("android", StringComparison.OrdinalIgnoreCase))
                        {
                            chosen = a;
                            chosenName = name;
                            break;
                        }
                    }
                }

                if (chosen == null || chosenName == null)
                    throw new InvalidOperationException("Release 中未找到 Windows NapCat.Shell zip");

                var url = chosen.Value.TryGetProperty("browser_download_url", out var u)
                    ? u.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(url))
                    throw new InvalidOperationException("资源缺少 browser_download_url");

                return (tag, chosenName, url);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                _log($"解析 Release 失败 ({apiUrl}): {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "无法获取 NapCat 最新版本信息。请检查网络或稍后重试。\n" +
            (last != null ? last.Message : ""));
    }

    private async Task DownloadFileWithProgressAsync(string url, string destPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long read = 0;
        int lastPct = -1;
        int n;
        while ((n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total is > 0)
            {
                var pct = (int)(read * 100 / total.Value);
                if (pct != lastPct && (pct % 10 == 0 || pct == 100))
                {
                    lastPct = pct;
                    _log($"  进度 {pct}%  ({FormatSize(read)} / {FormatSize(total.Value)})");
                }
            }
            else if (read is 5_000_000 or 20_000_000 or 50_000_000)
            {
                _log($"  已下载 {FormatSize(read)}…");
            }
        }
    }

    private static string? FindShellInTree(string root)
    {
        try
        {
            var boot = Directory.EnumerateFiles(root, "NapCatWinBootMain.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (boot == null) return null;
            var dir = Path.GetDirectoryName(boot)!;
            return NapCatLauncher.IsShellDirectory(dir) ? dir : null;
        }
        catch
        {
            return null;
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    public static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }
}
