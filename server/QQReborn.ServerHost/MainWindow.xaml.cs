using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QQReborn.ServerHost;

public partial class MainWindow : Window
{
    private readonly ServerProcessManager _mgr = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Brush _ok;
    private readonly Brush _bad;
    private readonly ObservableCollection<AccountOption> _accounts = new();
    private bool _suppressAccountChange;

    public MainWindow()
    {
        InitializeComponent();
        _ok = (Brush)FindResource("Ok");
        _bad = (Brush)FindResource("Bad");

        RepoRootText.Text = _mgr.RepoRoot;
        AccessPasswordBox.Text = _mgr.AccessPassword;

        LoadAccountsIntoUi();

        _mgr.LogLine += line => Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => RefreshStatus();
        _uiTimer.Start();

        Append("Repo: " + _mgr.RepoRoot);
        Append(_mgr.DescribeNapCatPaths());
        Append($"当前账号: {_mgr.NapCatUinDisplay}");
        Append("推荐：点上方「一键安装并启动」完成下载 NapCat + 配置 + 开网关。");
        Append("Shell 只需填写地址、端口和此处的访问密码。");
        RefreshStatus();
        _ = ProbeNapCatQuietAsync();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        OneClickBtn.IsEnabled = !busy;
        InstallOnlyBtn.IsEnabled = !busy;
        StartAllBtn.IsEnabled = !busy;
        StartNapCatBtn.IsEnabled = !busy;
        RestartNapCatBtn.IsEnabled = !busy;
        if (status != null) OneClickStatus.Text = status;
    }

    private async void OneClick_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "进行中…");
        NapCatStatus.Text = "准备中…";
        NapCatDot.Fill = _bad;
        try
        {
            PullUiIntoManager();
            Append("—— 一键安装并启动 ——");
            var result = await _mgr.OneClickSetupAsync();
            AccessPasswordBox.Text = _mgr.AccessPassword;
            OneClickStatus.Text = result.Success
                ? (result.NapCatOnline ? "完成 · NapCat 在线" : "完成 · 等待 QQ 登录")
                : "失败";
            NapCatStatus.Text = result.NapCatOnline ? "在线" : (result.Success ? "已启动，等待登录" : "未连接");
            NapCatDot.Fill = result.NapCatOnline ? _ok : _bad;

            if (result.Success)
            {
                MessageBox.Show(
                    result.Message + "\n\n" +
                    $"访问密码: {_mgr.AccessPassword}\n" +
                    $"网关端口: {_mgr.RealServerPort}\n" +
                    "手机 Shell 填本机 IP / 端口 / 密码即可。",
                    "一键安装并启动",
                    MessageBoxButton.OK,
                    result.NapCatOnline ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(result.Message, "一键安装未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            OneClickStatus.Text = "失败";
            Append("一键流程异常: " + ex.Message);
            MessageBox.Show(ex.Message, "一键安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private async void InstallOnly_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "安装 NapCat…");
        try
        {
            PullUiIntoManager();
            await _mgr.InstallOrUpdateNapCatAsync();
            OneClickStatus.Text = "NapCat 已安装";
            MessageBox.Show(
                "NapCat 已下载/更新并写入 OneBot 配置（HTTP 3000 / WS 3001）。\n" +
                "可继续点「一键安装并启动」或「启动 NapCat」。",
                "安装完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            OneClickStatus.Text = "安装失败";
            Append("安装 NapCat 失败: " + ex.Message);
            MessageBox.Show(ex.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private void LoadAccountsIntoUi()
    {
        _suppressAccountChange = true;
        _accounts.Clear();
        foreach (var a in _mgr.Accounts)
            _accounts.Add(a);
        // Always ensure QR option at end
        if (!_accounts.Any(a => string.IsNullOrEmpty(a.Uin)))
            _accounts.Add(AccountOption.QrScan);

        AccountCombo.ItemsSource = _accounts;
        var selected = _accounts.FirstOrDefault(a => a.Uin == _mgr.NapCatUin)
                       ?? _accounts.FirstOrDefault(a => a.Uin == AccountOption.Main.Uin)
                       ?? _accounts[0];
        AccountCombo.SelectedItem = selected;
        _mgr.NapCatUin = selected.Uin;
        _suppressAccountChange = false;
    }

    private void PullUiIntoManager()
    {
        _mgr.AccessPassword = AccessPasswordBox.Text.Trim();
        _mgr.Backend = "napcat";
        if (AccountCombo.SelectedItem is AccountOption opt)
            _mgr.NapCatUin = opt.Uin ?? "";
        _mgr.Accounts = _accounts.ToList();
    }

    private async Task ProbeNapCatQuietAsync()
    {
        var ok = await _mgr.CheckNapCatAsync();
        Dispatcher.Invoke(() =>
        {
            NapCatStatus.Text = ok ? "在线" : "未连接";
            NapCatDot.Fill = ok ? _ok : _bad;
        });
    }

    private void RefreshStatus()
    {
        var rs = _mgr.RealServerRunning;
        RealDot.Fill = rs ? _ok : _bad;
        RealStatus.Text = rs ? "运行中" : "已停止";
        RealUrl.Text = $"ws://127.0.0.1:{_mgr.RealServerPort}/ws";
        NapCatUrl.Text = _mgr.NapCatHttp.TrimEnd('/') + "/get_login_info";
    }

    private void AccountCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressAccountChange) return;
        if (AccountCombo.SelectedItem is AccountOption opt)
        {
            _mgr.NapCatUin = opt.Uin ?? "";
            Append("已选择账号: " + opt.Display);
        }
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e) => TryAddCustomAccount();

    private void CustomUinBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TryAddCustomAccount();
        }
    }

    private void TryAddCustomAccount()
    {
        var raw = CustomUinBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            MessageBox.Show("请输入 QQ 号。", "账号", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Regex.IsMatch(raw, @"^\d{5,12}$"))
        {
            MessageBox.Show("QQ 号应为 5–12 位数字。", "账号", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existing = _accounts.FirstOrDefault(a => a.Uin == raw);
        if (existing != null)
        {
            AccountCombo.SelectedItem = existing;
            CustomUinBox.Clear();
            return;
        }

        var item = new AccountOption { Uin = raw, Label = "自定义" };
        // Insert before QR option
        var qrIdx = -1;
        for (var i = 0; i < _accounts.Count; i++)
        {
            if (string.IsNullOrEmpty(_accounts[i].Uin)) { qrIdx = i; break; }
        }
        if (qrIdx >= 0) _accounts.Insert(qrIdx, item);
        else _accounts.Add(item);

        AccountCombo.SelectedItem = item;
        CustomUinBox.Clear();
        PullUiIntoManager();
        _mgr.SaveSettings();
        Append("已加入账号: " + item.Display);
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "启动网关…");
        try
        {
            PullUiIntoManager();
            if (string.IsNullOrWhiteSpace(_mgr.AccessPassword))
                throw new InvalidOperationException("请先设置网关访问密码。");
            Append("—— 启动中 (napcat local gateway) ——");
            Append("持号账号: " + _mgr.NapCatUinDisplay);
            await _mgr.StartAllAsync();
            var ok = await _mgr.CheckNapCatAsync();
            NapCatStatus.Text = ok ? "在线" : "未连接";
            NapCatDot.Fill = ok ? _ok : _bad;
            OneClickStatus.Text = ok ? "网关运行中" : "网关已启 · NapCat 待登录";
        }
        catch (Exception ex)
        {
            Append("启动失败: " + ex.Message);
            MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _mgr.StopAll();
        RefreshStatus();
    }

    private async void StartNapCat_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "启动 NapCat…");
        NapCatStatus.Text = "启动中…";
        NapCatDot.Fill = _bad;
        try
        {
            PullUiIntoManager();
            Append("—— 启动 NapCat ——");
            Append("持号账号: " + _mgr.NapCatUinDisplay);
            var ok = await _mgr.StartNapCatAsync(waitForOnline: true);
            NapCatStatus.Text = ok ? "在线" : "未连接";
            NapCatDot.Fill = ok ? _ok : _bad;
            OneClickStatus.Text = ok ? "NapCat 在线" : "等待 QQ 登录";
            if (!ok)
            {
                MessageBox.Show(
                    "NapCat 进程已尝试启动，但 OneBot HTTP 尚未就绪。\n请在弹出的 QQ 窗口完成登录后，再点「检测 NapCat」。",
                    "NapCat", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Append("启动 NapCat 失败: " + ex.Message);
            NapCatStatus.Text = "未连接";
            MessageBox.Show(ex.Message, "启动 NapCat 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private async void RestartNapCat_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "重启 NapCat…");
        NapCatStatus.Text = "重启中…";
        NapCatDot.Fill = _bad;
        try
        {
            PullUiIntoManager();
            Append("—— 重启 NapCat 并切换账号 ——");
            Append("目标账号: " + _mgr.NapCatUinDisplay);
            // Restarting a logged-out account must not hold the steward UI in a 90-second
            // polling loop. The user can complete QQ/QR login in the foreground and use
            // "检测 NapCat" once the OneBot endpoint is ready.
            var ok = await _mgr.RestartNapCatAsync(waitForOnline: false);
            NapCatStatus.Text = ok ? "在线" : "已启动，等待登录";
            NapCatDot.Fill = ok ? _ok : _bad;
            OneClickStatus.Text = ok ? "NapCat 在线" : "已重启 · 等待登录";
        }
        catch (Exception ex)
        {
            Append("重启 NapCat 失败: " + ex.Message);
            NapCatStatus.Text = "未连接";
            MessageBox.Show(ex.Message, "重启 NapCat 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private async void CheckNapCat_Click(object sender, RoutedEventArgs e)
    {
        NapCatStatus.Text = "检测中…";
        NapCatDot.Fill = _bad;
        var ok = await _mgr.CheckNapCatAsync();
        NapCatStatus.Text = ok ? "在线" : "未连接";
        NapCatDot.Fill = ok ? _ok : _bad;
    }

    private void EnsureConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PullUiIntoManager();
            _mgr.EnsureNapCatConfigOnly();
            MessageBox.Show(
                "已写入/校正 OneBot 配置：\nHTTP 127.0.0.1:3000\nWS 127.0.0.1:3001\n\n" +
                "若 NapCat 已在运行，请重启 NapCat 使配置生效。",
                "OneBot 配置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "写入配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(AccessPasswordBox.Text);
        Append("已复制访问密码。");
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        var text = $"主机 127.0.0.1  端口 {_mgr.RealServerPort}  密码 {AccessPasswordBox.Text}";
        Clipboard.SetText(text);
        Append("已复制: " + text);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void RealUrl_Click(object sender, MouseButtonEventArgs e)
    {
        Clipboard.SetText(RealUrl.Text);
        Append("已复制: " + RealUrl.Text);
    }

    private void NapCatUrl_Click(object sender, MouseButtonEventArgs e)
    {
        Clipboard.SetText(NapCatUrl.Text);
        Append("已复制: " + NapCatUrl.Text);
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer.Stop();
        PullUiIntoManager();
        _mgr.SaveSettings();
        if (_mgr.RealServerRunning)
        {
            var r = MessageBox.Show("关闭面板时是否停止网关？", "退出",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes) _mgr.StopAll();
        }
        _mgr.Dispose();
    }

    private void Append(string line)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
