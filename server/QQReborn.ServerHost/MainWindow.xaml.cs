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
        Append("本机 NapCat 网关管家。选账号 → 启动 NapCat / 启动网关。");
        Append("Shell 只需填写地址、端口和此处的访问密码。");
        RefreshStatus();
        _ = ProbeNapCatQuietAsync();
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
        StartAllBtn.IsEnabled = false;
        StartNapCatBtn.IsEnabled = false;
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
        }
        catch (Exception ex)
        {
            Append("启动失败: " + ex.Message);
            MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StartAllBtn.IsEnabled = true;
            StartNapCatBtn.IsEnabled = true;
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
        StartNapCatBtn.IsEnabled = false;
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
            StartNapCatBtn.IsEnabled = true;
            RefreshStatus();
        }
    }

    private async void RestartNapCat_Click(object sender, RoutedEventArgs e)
    {
        StartAllBtn.IsEnabled = false;
        StartNapCatBtn.IsEnabled = false;
        RestartNapCatBtn.IsEnabled = false;
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
        }
        catch (Exception ex)
        {
            Append("重启 NapCat 失败: " + ex.Message);
            NapCatStatus.Text = "未连接";
            MessageBox.Show(ex.Message, "重启 NapCat 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StartAllBtn.IsEnabled = true;
            StartNapCatBtn.IsEnabled = true;
            RestartNapCatBtn.IsEnabled = true;
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
