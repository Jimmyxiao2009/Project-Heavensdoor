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
        _mgr.LogLine += line => Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => RefreshStatus();
        _uiTimer.Start();

        Append("Repo: " + _mgr.RepoRoot);
        Append("选择后端（Lagrange / NapCat）后点「全部启动」。");
        Append("文档: docs/BACKEND-SWITCH.md");
        RefreshStatus();
    }

    private void ApplyBackendSelection()
    {
        if (BackendCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item
            && item.Tag is string tag
            && !string.IsNullOrWhiteSpace(tag))
        {
            _mgr.Backend = tag;
        }
        else
        {
            _mgr.Backend = "lagrange";
        }
    }

    private void RefreshStatus()
    {
        var rs = _mgr.RealServerRunning;
        var sp = _mgr.SignProxyRunning;
        RealDot.Fill = rs ? _ok : _bad;
        SignDot.Fill = sp ? _ok : _bad;
        RealStatus.Text = rs ? "运行中" : "已停止";
        SignStatus.Text = sp ? "运行中" : "已停止";
        RealUrl.Text = $"ws://127.0.0.1:{_mgr.RealServerPort}/ws";
        SignUrl.Text = $"http://127.0.0.1:{_mgr.SignProxyPort}";
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        StartAllBtn.IsEnabled = false;
        try
        {
            ApplyBackendSelection();
            Append("—— 启动中 (backend=" + _mgr.Backend + ") ——");
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

    private async void WebhookTest_Click(object sender, RoutedEventArgs e)
    {
        if (!_mgr.RealServerRunning)
        {
            MessageBox.Show("请先启动 RealServer。", "Webhook", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await _mgr.TestSpaceWebhookAsync();
    }

    private void WebhookHelp_Click(object sender, RoutedEventArgs e)
    {
        var help =
            "空间动态 Webhook\n\n" +
            $"POST http://127.0.0.1:{_mgr.RealServerPort}/webhook/space\n" +
            "Content-Type: application/json\n\n" +
            "{\n" +
            "  \"author\": \"张三\",\n" +
            "  \"text\": \"内容\",\n" +
            "  \"images\": [\"https://...\"],\n" +
            "  \"time\": \"2026-07-21T12:00:00+08:00\"\n" +
            "}\n\n" +
            "或 { \"items\": [ {...}, {...} ] }\n\n" +
            "App「动态」页通过 getMoments 拉取；\n" +
            "Web 空间 / 爬虫向此 URL POST 即可。";
        MessageBox.Show(help, "Webhook 说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void RealUrl_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Clipboard.SetText(RealUrl.Text);
        Append("已复制: " + RealUrl.Text);
    }

    private void SignUrl_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Clipboard.SetText(SignUrl.Text);
        Append("已复制: " + SignUrl.Text);
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer.Stop();
        // Ask whether to stop servers
        if (_mgr.RealServerRunning || _mgr.SignProxyRunning)
        {
            var r = MessageBox.Show("关闭面板时是否停止已启动的服务？", "退出",
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
