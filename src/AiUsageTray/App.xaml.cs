using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Threading;
using AiUsageTray.Models;
using AiUsageTray.Services;
using AiUsageTray.Views;
using Forms = System.Windows.Forms;

namespace AiUsageTray;

public partial class App : Application
{
    private Forms.NotifyIcon _tray = null!;
    private FlyoutWindow _flyout = null!;
    private WidgetBarWindow? _widgetBar;
    private SettingsWindow? _settingsWindow;
    private AppSettings _settings = null!;
    private UsageMonitor _monitor = null!;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _promotionProbe;
    private DispatcherTimer? _iconTimer;

    /// <summary>아이콘에 번갈아 띄울 도구들. 하나뿐이면 전환하지 않는다.</summary>
    private IReadOnlyList<ProviderUsage> _iconRotation = Array.Empty<ProviderUsage>();
    private int _iconIndex;
    private System.Drawing.Icon? _currentIcon;

    /// <summary>같은 앱이 두 번 뜨지 않게 한다.</summary>
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, "AiQuotaTray.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show(Strings.Get("app.alreadyRunning"),
                Strings.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();

        // 첫 실행이면 Windows 표시 언어를 따르고, 설치된 도구만 켠다.
        if (string.IsNullOrEmpty(_settings.Language))
            _settings.Language = Strings.DetectSystemLanguage();

        if (!_settings.SetupCompleted)
            _settings.DetectInstalledTools();

        Strings.Current = _settings.Language;
        _monitor = new UsageMonitor(_settings);

        // 이벤트는 백그라운드 스레드에서 올 수 있으므로 UI 스레드로 넘긴다.
        _monitor.Updated += usages => Dispatcher.Invoke(() => OnUsageUpdated(usages));
        _monitor.BusyChanged += busy => Dispatcher.Invoke(() => _flyout.SetBusy(busy));

        BuildFlyout();
        BuildWidgetBar();
        BuildTray();

        // 언어를 바꾸면 이미 만들어둔 UI도 새 말로 갈아끼워야 한다.
        Strings.Changed += OnLanguageChanged;
        RestartTimer();
        SyncTaskbarPromotion();

        // 첫 실행이면 설정을 먼저 보여준다.
        if (!_settings.SetupCompleted)
            OpenSettings();

        // --flyout: 트레이를 거치지 않고 바로 펼친다. 확인·디버깅용.
        bool showNow = e.Args.Any(a =>
            string.Equals(a, "--flyout", StringComparison.OrdinalIgnoreCase));

        _ = _monitor.RefreshAsync().ContinueWith(_ =>
        {
            if (showNow) Dispatcher.Invoke(ToggleFlyout);
        }, TaskScheduler.Default);
    }

    // ---- 구성 ----

    private void BuildFlyout()
    {
        _flyout = new FlyoutWindow();
        _flyout.RefreshRequested += () => _ = _monitor.RefreshAsync(force: true);
        _flyout.SettingsRequested += OpenSettings;
        ApplyDisplaySettings();
    }

    /// <summary>설정이 바뀌면 화면에 반영한다.</summary>
    private void ApplyDisplaySettings()
    {
        _flyout.DisplayMode = _settings.DisplayMode;
        _flyout.ColorResolver = _settings.ColorFor;
        _flyout.StatusResolver = _monitor.Status.For;
    }

    /// <summary>위젯바를 만든다. 설정이 꺼져 있으면 만들지 않는다.</summary>
    private void BuildWidgetBar()
    {
        if (!_settings.ShowWidgetBar) return;

        _widgetBar = new WidgetBarWindow();
        _widgetBar.Clicked += ToggleFlyout;
        ApplyWidgetSettings();
    }

    /// <summary>설정이 바뀌면 위젯바를 켜거나 끈다.</summary>
    private void SyncWidgetBar()
    {
        if (_settings.ShowWidgetBar)
        {
            if (_widgetBar is null) BuildWidgetBar();

            ApplyWidgetSettings();
            _widgetBar?.Render(_monitor.Latest);
        }
        else if (_widgetBar is not null)
        {
            _widgetBar.Close();
            _widgetBar = null;
        }
    }

    private void ApplyWidgetSettings()
    {
        if (_widgetBar is null) return;

        _widgetBar.DisplayMode = _settings.DisplayMode;
        _widgetBar.ColorResolver = _settings.ColorFor;
        _widgetBar.StatusResolver = _monitor.Status.For;
    }

    private void BuildTray()
    {
        _currentIcon = TrayIconRenderer.Render(null, IconSize());

        _tray = new Forms.NotifyIcon
        {
            Icon = _currentIcon,
            Text = Strings.Get("app.name"),
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        // 좌클릭으로 열고 닫는다. 우클릭은 메뉴가 알아서 처리한다.
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ToggleFlyout();
        };
    }

    /// <summary>
    /// 설정에 맞춰 작업표시줄 고정을 적용한다.
    ///
    /// Windows는 트레이 아이콘을 처음 본 뒤에야 레지스트리 항목을 만든다.
    /// 언제 만들지는 Explorer가 정하고, 경우에 따라 한참 뒤이거나 Explorer가
    /// 다시 시작한 뒤일 수도 있다. 그래서 짧게 재시도하다 포기하지 않고,
    /// 항목이 나타날 때까지 간격을 늘려가며 계속 지켜본다.
    /// </summary>
    private void SyncTaskbarPromotion()
    {
        if (!TaskbarPromotion.IsSupported) return;

        _promotionProbe?.Stop();

        int attempts = 0;
        var probe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _promotionProbe = probe;

        probe.Tick += (_, _) =>
        {
            attempts++;

            if (TaskbarPromotion.IsPromoted() is { } current)
            {
                if (current != _settings.ShowInTaskbar)
                    TaskbarPromotion.SetPromoted(_settings.ShowInTaskbar);

                probe.Stop();
                _promotionProbe = null;
                return;
            }

            // 처음 30초는 촘촘히, 그 뒤로는 1분 간격으로 느슨하게 지켜본다.
            // Explorer가 재시작되면 그때 항목이 생기므로 감시를 멈추지 않는다.
            if (attempts == 15) probe.Interval = TimeSpan.FromMinutes(1);
        };

        probe.Start();
    }

    /// <summary>트레이 우클릭 메뉴. 언어가 바뀌면 다시 만든다.</summary>
    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Strings.Get("menu.open"), null, (_, _) => ToggleFlyout());
        menu.Items.Add(Strings.Get("menu.refresh"), null, (_, _) => _ = _monitor.RefreshAsync(force: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Strings.Get("menu.settings"), null, (_, _) => OpenSettings());
        menu.Items.Add(Strings.Get("menu.issues"), null, (_, _) => OpenIssues());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Strings.Get("menu.quit"), null, (_, _) => QuitApp());
        return menu;
    }

    /// <summary>언어가 바뀌었을 때 화면 전체를 새 말로 다시 그린다.</summary>
    private void OnLanguageChanged()
    {
        // 트레이 메뉴는 문자열을 품고 있어 다시 만드는 편이 확실하다.
        if (_tray?.ContextMenuStrip is { } old)
        {
            _tray.ContextMenuStrip = BuildMenu();
            old.Dispose();
        }

        if (_tray is not null) _tray.Text = BuildTooltip(_monitor.Latest);

        _flyout.Retranslate();
        _flyout.Render(_monitor.Latest);
        _widgetBar?.Render(_monitor.Latest);
    }

    /// <summary>DPI에 맞는 트레이 아이콘 크기. 고DPI에서 흐릿해지지 않게.</summary>
    private static int IconSize()
    {
        int size = Forms.SystemInformation.SmallIconSize.Width;
        return size <= 0 ? 16 : size;
    }

    private void RestartTimer()
    {
        _timer?.Stop();
        _timer = null;

        if (_settings.RefreshIntervalSeconds <= 0) return;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds),
        };
        _timer.Tick += (_, _) => _ = _monitor.RefreshAsync();
        _timer.Start();
    }

    // ---- 동작 ----

    /// <summary>
    /// 팝업을 열거나 닫는다.
    ///
    /// 위젯바나 트레이 아이콘을 누르면 팝업이 포커스를 잃어 스스로 먼저 닫힌다.
    /// 그 직후 이 메서드가 불리면 "닫혀 있으니 열자"가 되어 토글이 되지 않는다.
    /// 방금 닫힌 것이라면 그 클릭은 닫으려는 의도였다고 보고 다시 열지 않는다.
    /// </summary>
    private void ToggleFlyout()
    {
        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            return;
        }

        if (DateTime.Now - _flyout.HiddenAt < FlyoutReopenGuard) return;

        _flyout.Render(_monitor.Latest);
        _flyout.ShowNearTray();

        // 값이 오래됐을 때만 다시 가져온다. 연달아 열어도 서버를 두드리지 않는다.
        _ = _monitor.RefreshAsync();
    }

    /// <summary>이 시간 안에 닫힌 팝업은 같은 클릭으로 닫힌 것으로 본다.</summary>
    private static readonly TimeSpan FlyoutReopenGuard = TimeSpan.FromMilliseconds(250);

    private void OnUsageUpdated(IReadOnlyList<ProviderUsage> usages)
    {
        if (_flyout.IsVisible)
            _flyout.Render(usages);

        if (_widgetBar is not null)
        {
            _widgetBar.Render(usages);

            // 보여줄 것이 생겼는데 아직 숨어 있으면 띄운다.
            if (!_widgetBar.IsVisible && usages.Any(u => u.IsAvailable && u.Windows.Count > 0))
            {
                _widgetBar.Show();
                _widgetBar.Reposition();
            }
        }

        UpdateTrayIcon(usages);
    }

    private void UpdateTrayIcon(IReadOnlyList<ProviderUsage> usages)
    {
        // 보여줄 수치가 있는 것만 아이콘 순환 대상이다.
        _iconRotation = usages.Where(u => u.IsAvailable && u.Windows.Count > 0).ToList();

        // 도구가 하나뿐이면 순환할 이유가 없으니 항상 그것만 보여준다.
        if (_iconIndex >= _iconRotation.Count) _iconIndex = 0;

        DrawCurrentIcon();
        RestartIconRotation();

        _tray.Text = BuildTooltip(usages);
    }

    /// <summary>지금 차례인 도구를 아이콘으로 그린다.</summary>
    private void DrawCurrentIcon()
    {
        Icon newIcon;

        if (_iconRotation.Count == 0)
        {
            // 아직 값이 없다. 빈 게이지로 둔다.
            newIcon = TrayIconRenderer.Render(null, IconSize());
        }
        else
        {
            var u = _iconRotation[Math.Min(_iconIndex, _iconRotation.Count - 1)];

            double used = u.PeakPercent;
            double shown = _settings.DisplayMode == DisplayMode.Remaining ? 100 - used : used;

            newIcon = TrayIconRenderer.RenderNumber(shown, ToDrawingColor(_settings.ColorFor(u.Provider)), IconSize());
        }

        var previous = _currentIcon;
        _currentIcon = newIcon;
        _tray.Icon = newIcon;

        // 아이콘을 교체한 뒤에 이전 것을 버려야 깜빡임이 없다.
        previous?.Dispose();
    }

    /// <summary>
    /// 도구가 둘 이상이면 몇 초 간격으로 번갈아 보여준다.
    /// 하나뿐이면 타이머를 돌리지 않는다.
    /// </summary>
    private void RestartIconRotation()
    {
        if (_iconRotation.Count < 2)
        {
            _iconTimer?.Stop();
            _iconTimer = null;
            return;
        }

        if (_iconTimer is not null) return; // 이미 돌고 있다.

        _iconTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _iconTimer.Tick += (_, _) =>
        {
            if (_iconRotation.Count < 2) return;
            _iconIndex = (_iconIndex + 1) % _iconRotation.Count;
            DrawCurrentIcon();
        };
        _iconTimer.Start();
    }

    /// <summary>설정의 #RRGGBB 문자열을 GDI 색으로. 잘못된 값이면 회색.</summary>
    private static System.Drawing.Color ToDrawingColor(string hex)
    {
        try
        {
            var c = (System.Windows.Media.Color)
                System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }
        catch
        {
            return System.Drawing.Color.FromArgb(0x8B, 0x8B, 0x8B);
        }
    }

    /// <summary>툴팁은 63자 제한이 있어 짧게 만든다.</summary>
    private string BuildTooltip(IReadOnlyList<ProviderUsage> usages)
    {
        bool remaining = _settings.DisplayMode == DisplayMode.Remaining;

        var parts = usages
            .Where(u => u.IsAvailable && u.Windows.Count > 0)
            .Select(u => u.Provider + " " + (remaining
                ? Strings.Get("value.remaining", $"{100 - u.PeakPercent:F0}")
                : Strings.Get("value.used", $"{u.PeakPercent:F0}")));

        string text = string.Join("  ", parts);
        if (string.IsNullOrEmpty(text)) text = Strings.Get("app.name");

        return text.Length > 62 ? text[..62] : text;
    }

    private void OpenSettings()
    {
        // 이미 열려 있으면 앞으로 가져온다.
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var win = new SettingsWindow(_settings);
        _settingsWindow = win;

        win.Closed += (_, _) => _settingsWindow = null;
        win.Saved += () =>
        {
            ApplyDisplaySettings();
            SyncWidgetBar();
            RestartTimer();

            // 켜고 끈 것을 화면에 바로 반영한다. 조회를 기다리지 않는다.
            _monitor.ApplyEnabledChange();

            // 그다음 실제 값을 다시 가져온다. 서버가 막혀 있어도 위에서
            // 되살린 캐시가 남아 있어 화면이 비지 않는다.
            _ = _monitor.RefreshAsync(force: true);

            if (_flyout.IsVisible) _flyout.Render(_monitor.Latest);
        };

        win.Show();
        win.Activate();
    }

    /// <summary>기본 브라우저로 이슈 페이지를 연다.</summary>
    internal static void OpenIssues()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.IssuesUrl) { UseShellExecute = true });
        }
        catch
        {
            // 브라우저가 없거나 정책으로 막힌 환경. 알릴 만한 일은 아니다.
        }
    }

    private void QuitApp()
    {
        _tray.Visible = false;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _promotionProbe?.Stop();
        _iconTimer?.Stop();
        _monitor?.Dispose();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _widgetBar?.Close();
        _currentIcon?.Dispose();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}
