using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace QQReborn.ServerHost;

public partial class MainWindow : Window
{
    private readonly ServerProcessManager _mgr = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Brush _ok;
    private readonly Brush _bad;

    public MainWindow()
    {
        InitializeComponent();
        _ok = (Brush)FindResource("Ok");
        _bad = (Brush)FindResource("Bad");

        RepoRootText.Text = _mgr.RepoRoot;
        AccessPasswordBox.Text = _mgr.AccessPassword;
        _mgr.LogLine += line => Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => RefreshStatus();
        _uiTimer.Start();

        Append("Repo: " + _mgr.RepoRoot);
        Append("本机 NapCat 网关管家。请确认 NTQQ + NapCat 已登录，再点「启动网关」。");
        Append("Shell 只需填写地址、端口和此处的访问密码。");
        RefreshStatus();
        _ = ProbeNapCatQuietAsync();
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

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        StartAllBtn.IsEnabled = false;
        try
        {
            _mgr.Backend = "napcat";
            _mgr.AccessPassword = AccessPasswordBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(_mgr.AccessPassword))
                throw new InvalidOperationException("请先设置网关访问密码。");
            Append("—— 启动中 (napcat local gateway) ——");
            await _mgr.StartAllAsync();
        }
        catch (Exception ex)
        {
            Append("启动失败: " + ex.Message);
            MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StartAllBtn.IsEnabled = true;
            RefreshStatus();
        }
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _mgr.StopAll();
        RefreshStatus();
    }

    private async void CheckNapCat_Click(object sender, RoutedEventArgs e)
    {
        NapCatStatus.Text = "检测中…";
        NapCatDot.Fill = _bad;
        var ok = await _mgr.CheckNapCatAsync();
        NapCatStatus.Text = ok ? "在线" : "未连接";
        NapCatDot.Fill = ok ? _ok : _bad;
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

    private void RealUrl_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Clipboard.SetText(RealUrl.Text);
        Append("已复制: " + RealUrl.Text);
    }

    private void NapCatUrl_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Clipboard.SetText(NapCatUrl.Text);
        Append("已复制: " + NapCatUrl.Text);
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer.Stop();
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
