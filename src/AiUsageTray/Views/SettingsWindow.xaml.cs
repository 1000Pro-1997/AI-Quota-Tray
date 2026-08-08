using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiUsageTray.Services;

namespace AiUsageTray.Views;

public partial class SettingsWindow : Window
{
    private const string ThemeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string RunValueName = "AiQuotaTray";

    private readonly AppSettings _settings;

    /// <summary>저장 버튼을 눌러 설정이 바뀌었을 때 발생.</summary>
    public event Action? Saved;

    /// <summary>색상 선택 대화상자에서 고른 값을 저장 전까지 들고 있는다.</summary>
    private string _claudeColor = AppSettings.DefaultClaudeColor;
    private string _codexColor = AppSettings.DefaultCodexColor;

    /// <summary>고를 수 있는 새로고침 주기(초). 0은 자동 갱신 없음.</summary>
    private static readonly int[] Intervals = { 60, 300, 600, 1800, 0 };

    private readonly UpdateChecker? _updates;

    private System.Windows.Forms.Screen[] _widgetScreens = Array.Empty<System.Windows.Forms.Screen>();

    public SettingsWindow(AppSettings settings, UpdateChecker? updates = null)
    {
        _settings = settings;
        _updates = updates;
        InitializeComponent();
        ApplyTheme();
        LoadFromSettings();
    }

    // ---- 테마 ----

    private void ApplyTheme()
    {
        bool dark = IsSystemDarkTheme();
        void Set(string key, Color c) => Resources[key] = new SolidColorBrush(c);

        if (dark)
        {
            Set("WindowBrush", Color.FromRgb(0x1C, 0x1C, 0x1C));
            Set("CardBrush", Color.FromRgb(0x27, 0x27, 0x27));
            Set("BorderBrush2", Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
            Set("TextBrush", Color.FromRgb(0xF2, 0xF2, 0xF2));
            Set("SubtleBrush", Color.FromRgb(0x94, 0x94, 0x94));
            Set("TrackOffBrush", Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            Set("SelectedBrush", Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
            Set("HoverBrush", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            Set("HoverStrongBrush", Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            Set("InputBrush", Color.FromRgb(0x1F, 0x1F, 0x1F));
            Set("AccentBrush", Color.FromRgb(0x4C, 0x8E, 0xF0));
        }
        else
        {
            Set("WindowBrush", Color.FromRgb(0xF5, 0xF5, 0xF7));
            Set("CardBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            Set("BorderBrush2", Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
            Set("TextBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            Set("SubtleBrush", Color.FromRgb(0x70, 0x70, 0x74));
            Set("TrackOffBrush", Color.FromArgb(0x18, 0x00, 0x00, 0x00));
            Set("SelectedBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            Set("HoverBrush", Color.FromArgb(0x10, 0x00, 0x00, 0x00));
            Set("HoverStrongBrush", Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
            Set("InputBrush", Color.FromRgb(0xFA, 0xFA, 0xFA));
            Set("AccentBrush", Color.FromRgb(0x2F, 0x7C, 0xEA));
        }
    }

    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ThemeKeyPath);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---- 불러오기 / 저장 ----

    /// <summary>화면을 채우는 중에는 저장을 부르지 않는다.</summary>
    private bool _loading;

    private void LoadFromSettings()
    {
        _loading = true;

        ClaudeEnabled.IsChecked = _settings.ClaudeEnabled;
        CodexEnabled.IsChecked = _settings.CodexEnabled;

        // 비어 있으면 자동 탐지된 기본 경로를 보여준다. 사용자가 무엇을 읽는지 알 수 있게.
        ClaudePath.Text = _settings.EffectiveClaudePath;
        CodexPath.Text = _settings.EffectiveCodexPath;

        StartWithWindows.IsChecked = _settings.StartWithWindows;
        ClaudePrimeFive.IsChecked = _settings.ClaudePrimeFiveHour;
        ClaudePrimeWeekly.IsChecked = _settings.ClaudePrimeWeekly;
        CodexPrimeFive.IsChecked = _settings.CodexPrimeFiveHour;
        CodexPrimeWeekly.IsChecked = _settings.CodexPrimeWeekly;
        AutoUpdateEnabled.IsChecked = _settings.AutoUpdate;

        // 실제 레지스트리 상태를 우선한다. 사용자가 Windows 설정에서
        // 직접 바꿨을 수 있으므로 저장된 값보다 현재 상태가 정확하다.
        PinToTaskbar.IsChecked = TaskbarPromotion.IsPromoted() ?? _settings.ShowInTaskbar;
        ShowWidgetBar.IsChecked = _settings.ShowWidgetBar;
        WidgetAutoOffset.IsChecked = _settings.WidgetAutoOffset;
        WidgetOffsetX.Text = _settings.WidgetOffsetX.ToString();
        WidgetOffsetY.Text = _settings.WidgetOffsetY.ToString();
        ShowOffsetRow();
        BuildWidgetMonitorList();

        if (!TaskbarPromotion.IsSupported)
        {
            PinToTaskbar.IsEnabled = false;
            TaskbarHint.Text = Strings.Get("settings.pinUnsupported");
        }
        else if (TaskbarPromotion.IsPromoted() is null)
        {
            TaskbarHint.Text = Strings.Get("settings.pinPending");
        }

        BuildLanguageList();
        Retranslate();
        BuildIntervalSegments();

        // 표기 모드는 두 칸짜리 세그먼트다.
        if (_settings.DisplayMode == DisplayMode.Used) ModeUsed.IsChecked = true;
        else ModeRemaining.IsChecked = true;

        _claudeColor = _settings.ClaudeColor;
        _codexColor = _settings.CodexColor;
        UpdateSwatches();

        VersionText.Text = AppInfo.Version;
        ShowUpdateState(_updates?.Last);

        UpdatePathStatus();
        ShowInstallState();

        // 화면을 다 채웠으니 이제부터의 변경은 사용자 조작이다.
        _loading = false;
    }

    /// <summary>
    /// 작은 화면에서는 창을 그만큼 줄인다.
    ///
    /// 높이는 XAML에서 620으로 못 박았다. 내용에 맞춰 늘리면 항목이 늘 때마다
    /// 창 크기가 달라지고, 어느 순간 작업 표시줄 아래로 내려가 마지막 줄에
    /// 손이 닿지 않는다. 고정해 두면 넘치는 만큼은 늘 ScrollViewer가 맡는다.
    ///
    /// 다만 620이 안 들어가는 화면도 있다. 그때만 화면에 맞춰 줄인다.
    /// </summary>
    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 이 창이 뜬 화면을 기준으로 삼는다. 모니터마다 크기가 다르다.
        var area = System.Windows.Forms.Screen.FromHandle(
            new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;

        double scale = GetDpiScale();
        double screenHeight = area.Height / scale;

        // 화면과 딱 맞으면 답답하다. 위아래로 조금 남긴다.
        double limit = screenHeight - 40;
        if (Height <= limit) return;

        Height = limit;

        // 창을 줄였으니 가운데로 다시 놓는다. 잘린 채 아래로 치우쳐 뜬다.
        Top = area.Top / scale + (screenHeight - limit) / 2;
    }

    /// <summary>화면 배율. 고DPI에서는 픽셀과 WPF 단위가 다르다.</summary>
    private double GetDpiScale()
    {
        var source = System.Windows.PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
    }

    private void OnOpenIssues(object sender, RoutedEventArgs e) => App.OpenIssues();

    private void OnOpenGitHub(object sender, RoutedEventArgs e) => App.OpenUrl(AppInfo.Repository);

    /// <summary>화면이 여러 개일 때만 위젯을 옮길 대상을 고르게 한다.</summary>
    private void BuildWidgetMonitorList()
    {
        _widgetScreens = System.Windows.Forms.Screen.AllScreens;
        WidgetMonitorBox.Items.Clear();

        for (int i = 0; i < _widgetScreens.Length; i++)
        {
            var screen = _widgetScreens[i];
            string primary = screen.Primary ? $" ({Strings.Get("settings.primaryMonitor")})" : "";
            // \\.\DISPLAY2 같은 내부 경로는 저장에만 쓰고 화면에는 DISPLAY2만 보인다.
            string label = screen.DeviceName.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
                ?? screen.DeviceName;
            WidgetMonitorBox.Items.Add($"{label}{primary}");
        }

        int selected = Array.FindIndex(_widgetScreens, s => string.Equals(
            s.DeviceName, _settings.WidgetMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
        if (selected < 0) selected = Array.FindIndex(_widgetScreens, s => s.Primary);

        // 목록을 다시 세우는 것뿐인데 SelectionChanged가 ApplyNow를 부르면,
        // 아직 값을 못 채운 다른 컨트롤이 그대로 설정에 저장돼 기본값으로
        // 되돌아간 것처럼 보인다. 채우는 동안은 저장을 막는다.
        bool wasLoading = _loading;
        _loading = true;
        WidgetMonitorBox.SelectedIndex = selected >= 0 ? selected : 0;
        _loading = wasLoading;

        bool multiple = _widgetScreens.Length > 1;
        WidgetMonitorRow.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        WidgetMonitorDivider.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWidgetMonitorChanged(object sender, SelectionChangedEventArgs e) => ApplyNow();

    /// <summary>자동을 끄면 수동 오프셋 칸을 연다. 켜면 다시 감춘다.</summary>
    private void OnAutoOffsetToggled(object sender, RoutedEventArgs e)
    {
        ShowOffsetRow();
        ApplyNow();
    }

    private void OnOffsetTextChanged(object sender, TextChangedEventArgs e) => ApplyNow();

    /// <summary>수동 오프셋 칸은 자동을 꺼야 쓸모가 있다. 그때만 보인다.</summary>
    private void ShowOffsetRow()
    {
        bool manual = WidgetAutoOffset.IsChecked != true;
        WidgetOffsetRow.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>비어 있거나 숫자가 아니면 0으로 본다. 입력 중에도 튀지 않게.</summary>
    private static int ParseOffset(string text) =>
        int.TryParse(text.Trim(), out int value) ? Math.Clamp(value, -4000, 4000) : 0;

    /// <summary>설치 위치와 런처가 제자리에 있는지 보여 준다.</summary>
    private void ShowInstallState()
    {
        InstallPathText.Text = UpdateDownloader.InstallDir;

        bool ok = UpdateDownloader.LauncherInstalled;
        LauncherDot.Background = StatusDot(ok);
        LauncherStatus.Text = Strings.Get(ok
            ? "settings.launcherFound"
            : "settings.launcherMissing");

        // 고칠 수단은 고장났을 때만 보인다. 멀쩡한데 버튼이 있으면
        // 눌러야 하는 줄 안다.
        InstallLauncherButton.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        if (!ok) InstallLauncherButton.Content = Strings.Get("settings.installLauncher");
    }

    /// <summary>
    /// 런처를 지금 받아 앉힌다.
    ///
    /// 앱만 내려받아 쓰는 사람에게는 런처가 없다. 앱이 시작할 때 스스로
    /// 갖추지만 그때 인터넷이 없었을 수 있어, 손으로 다시 시킬 길을 둔다.
    /// </summary>
    private async void OnInstallLauncher(object sender, RoutedEventArgs e)
    {
        if (_updates is null) return;

        InstallLauncherButton.IsEnabled = false;
        InstallLauncherButton.Content = Strings.Get("settings.installingLauncher");

        try
        {
            // 어제 확인했더라도 지금 주소가 필요하다. 간격을 무시하고 묻는다.
            var info = await _updates.CheckAsync();

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            await new UpdateDownloader(http).EnsureLauncherAsync(info);
        }
        catch
        {
            // 실패하면 버튼이 그대로 남는다. 다시 누르면 된다.
        }
        finally
        {
            InstallLauncherButton.IsEnabled = true;
            InstallLauncherButton.Content = Strings.Get("settings.installLauncher");

            // 성공했으면 상태 줄과 버튼이 함께 정리된다.
            ShowInstallState();
        }
    }

    /// <summary>
    /// 설치 폴더를 탐색기로 연다. 폴더가 없으면 만들어서라도 연다.
    /// 아무 반응이 없으면 버튼이 고장 난 줄 알기 때문이다.
    /// </summary>
    private void OnOpenInstallFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(UpdateDownloader.InstallDir);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = UpdateDownloader.InstallDir,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 열지 못해도 설정 창에서 할 일은 남아 있다. 경로는 화면에 이미 있다.
        }
    }

    /// <summary>
    /// 버튼 하나가 상태에 따라 세 가지 일을 한다.
    /// 확인 → 내려받기 → 재시작. 사용자가 다음에 할 일만 보이게 하려는 것이다.
    ///
    /// 런처가 없으면 앱이 스스로를 교체할 수 없으므로 릴리스 페이지로 보낸다.
    /// </summary>
    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updates is null) return;

        // 이미 받아 둔 것이 있으면 남은 일은 재시작뿐이다.
        if (UpdateDownloader.PendingReady && UpdateDownloader.LauncherInstalled)
        {
            ApplyAndRestart();
            return;
        }

        if (_updates.Last is { HasUpdate: true } known)
        {
            // 릴리스에 받을 파일이 없을 때만 손으로 받게 한다.
            // 런처는 없으면 내려받는 김에 함께 갖춘다.
            if (!known.CanDownload)
            {
                App.OpenUrl(known.PageUrl);
                return;
            }

            await DownloadAsync(known);
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateButton.Content = Strings.Get("update.checking");
        UpdateDot.Visibility = Visibility.Collapsed;

        try
        {
            var info = await _updates.CheckAsync();

            // 이미 최신이어도 런처가 사라졌으면 여기서 되살린다. 새 버전이 나올
            // 때까지 기다리면 그동안은 부팅해도 앱이 안 뜬다. 확인 버튼을 누른
            // 사람은 "지금 멀쩡한지" 보려던 것이니 고쳐 두는 편이 맞다.
            if (!UpdateDownloader.LauncherInstalled && info.LauncherUrl.Length > 0)
            {
                UpdateButton.Content = Strings.Get("update.repairingLauncher");

                var repair = new UpdateDownloader(new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(5),
                });
                await repair.EnsureLauncherAsync(info);
                ShowInstallState();
            }

            ShowUpdateState(info);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    /// <summary>버튼이 차오르는 것으로 진행을 알리며 내려받는다.</summary>
    private async Task DownloadAsync(UpdateInfo info)
    {
        UpdateButton.IsEnabled = false;
        UpdateDot.Visibility = Visibility.Collapsed;
        SetProgress(0);

        var progress = new Progress<DownloadProgress>(p =>
        {
            UpdateButton.Content = Strings.Get("update.downloading", (int)p.Percent);
            SetProgress(p.Percent);
        });

        try
        {
            var downloader = new UpdateDownloader(new HttpClient
            {
                // 자립형 exe는 75MB쯤 된다. 기본 100초로는 느린 회선에서 끊긴다.
                Timeout = TimeSpan.FromMinutes(10),
            });

            // 교체를 맡을 런처가 없으면 먼저 갖춘다. 2MB라 금방 끝난다.
            await downloader.EnsureLauncherAsync(info);

            await downloader.DownloadAsync(info, progress);

            SetProgress(0);
            UpdateDot.Visibility = Visibility.Visible;

            // 다 받았으면 갈아끼우는 일만 남았다. 버튼을 한 번 더 누르게 하는 것은
            // 사용자에게 아무 선택도 주지 않으면서 손만 더 가게 하는 셈이라
            // 곧바로 재시작한다. 창을 닫아 버렸으면 다음 실행 때 적용된다.
            if (UpdateDownloader.LauncherInstalled)
            {
                UpdateButton.Content = Strings.Get("update.restarting");
                ApplyAndRestart();
                return;
            }

            UpdateButton.Content = Strings.Get("update.restart");
        }
        catch (OperationCanceledException)
        {
            // 앱이 내려가는 중이라 끊긴 것이지 실패한 것이 아니다. 여기서
            // "실패"를 띄우면 다음 실행에 멀쩡히 업데이트된 것과 어긋난다.
            SetProgress(0);
            UpdateButton.Content = Strings.Get("update.download");
        }
        catch (Exception ex)
        {
            SetProgress(0);
            UpdateButton.Content = ex is InvalidOperationException
                ? Strings.Get("update.download")
                : Strings.Get("update.downloadFailed");
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    /// <summary>런처에게 교체를 맡기고 앱을 끝낸다. 런처가 새 버전을 다시 띄운다.</summary>
    private void ApplyAndRestart()
    {
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = Strings.Get("update.restarting");

        if (UpdateDownloader.RestartToApply())
        {
            Application.Current.Shutdown();
            return;
        }

        // 런처를 못 띄웠다. 받아 둔 것은 다음 부팅에 적용되니 알리기만 한다.
        UpdateButton.Content = Strings.Get("update.noLauncher");
        UpdateButton.IsEnabled = true;
    }

    /// <summary>0~100을 막대 너비로 옮긴다.</summary>
    /// <summary>
    /// 버튼이 왼쪽부터 차오르는 정도를 정한다. 0이면 채움이 사라진다.
    ///
    /// ControlTemplate 안에서는 ActualWidth에 비율을 곱할 수 없어, 여기서
    /// 픽셀로 계산해 Tag에 넣는다. 템플릿의 채움 Border가 그 값을 너비로 쓴다.
    /// </summary>
    private void SetProgress(double percent)
    {
        double ratio = Math.Clamp(percent, 0, 100) / 100.0;
        UpdateButton.Tag = ratio <= 0 ? 0.0 : UpdateButton.ActualWidth * ratio;
    }

    /// <summary>확인 결과를 버전 줄에 반영한다.</summary>
    private void ShowUpdateState(UpdateInfo? info)
    {
        SetProgress(0);

        if (info is null)
        {
            // 아직 확인 전이다. 누르면 확인한다는 것만 알린다.
            UpdateDot.Visibility = Visibility.Collapsed;
            UpdateButton.Content = Strings.Get("update.check");
            return;
        }

        if (info.Error is not null)
        {
            // 사유는 길어서 버튼에 안 들어간다. 다시 눌러 보라는 뜻만 남긴다.
            UpdateDot.Visibility = Visibility.Collapsed;
            UpdateButton.Content = Strings.Get("update.check");
            return;
        }

        if (info.HasUpdate)
        {
            UpdateDot.Visibility = Visibility.Visible;
            UpdateButton.Content = UpdateDownloader.PendingReady
                ? Strings.Get("update.restart")
                : Strings.Get("update.download");
        }
        else
        {
            // 확인해 보니 최신이더라는 것까지 버튼이 말한다. 다시 누르면
            // 또 확인하므로 문구가 "확인"으로 돌아갈 필요는 없다.
            UpdateDot.Visibility = Visibility.Collapsed;
            UpdateButton.Content = Strings.Get("update.latest");
        }
    }

    /// <summary>지원 언어를 각자의 표기로 나열한다.</summary>
    private void BuildLanguageList()
    {
        _loadingLanguages = true;

        foreach (var (code, native) in Strings.Languages)
            LanguageBox.Items.Add(native);

        int idx = Array.FindIndex(Strings.Languages,
            l => string.Equals(l.Code, Strings.Current, StringComparison.OrdinalIgnoreCase));
        LanguageBox.SelectedIndex = idx >= 0 ? idx : 0;

        _loadingLanguages = false;
    }

    /// <summary>목록을 채우는 동안 생기는 선택 변경은 무시해야 한다.</summary>
    private bool _loadingLanguages;

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLanguages) return;

        int i = LanguageBox.SelectedIndex;
        if (i < 0 || i >= Strings.Languages.Length) return;

        // 바로 적용해 어떻게 보이는지 확인할 수 있게 한다.
        Strings.Current = Strings.Languages[i].Code;
        Retranslate();
        BuildWidgetMonitorList();
        UpdatePathStatus();
        ApplyNow();
    }

    /// <summary>현재 언어로 모든 문구를 다시 채운다.</summary>
    private void Retranslate()
    {
        Title = Strings.Get("settings.title");

        LblSectionTools.Text = Strings.Get("settings.sectionTools");
        LblSectionDisplay.Text = Strings.Get("settings.sectionDisplay");
        LblSectionBehavior.Text = Strings.Get("settings.sectionBehavior");

        PathToggle.Content = Strings.Get("settings.pathToggle");
        LblClaudeCred.Text = Strings.Get("settings.claudeCredential");
        LblCodexFolder.Text = Strings.Get("settings.codexFolder");
        BrowseClaude.Content = Strings.Get("settings.browse");
        BrowseCodex.Content = Strings.Get("settings.browse");

        LblLanguage.Text = Strings.Get("settings.language");
        LblLanguageHint.Text = Strings.Get("settings.languageHint");

        LblNumberFormat.Text = Strings.Get("settings.numberFormat");
        LblNumberFormatHint.Text = Strings.Get("settings.numberFormatHint");
        ModeRemaining.Content = Strings.Get("settings.modeRemaining");
        ModeUsed.Content = Strings.Get("settings.modeUsed");

        LblWidgetBar.Text = Strings.Get("settings.widgetBar");
        LblWidgetBarHint.Text = Strings.Get("settings.widgetBarHint");
        LblWidgetMonitor.Text = Strings.Get("settings.widgetMonitor");
        LblWidgetMonitorHint.Text = Strings.Get("settings.widgetMonitorHint");
        LblWidgetAutoOffset.Text = Strings.Get("settings.widgetAutoOffset");
        LblWidgetAutoOffsetHint.Text = Strings.Get("settings.widgetAutoOffsetHint");
        LblWidgetOffset.Text = Strings.Get("settings.widgetOffset");
        LblWidgetOffsetHint.Text = Strings.Get("settings.widgetOffsetHint");
        LblWidgetOffsetX.Text = Strings.Get("settings.widgetOffsetX");
        LblWidgetOffsetY.Text = Strings.Get("settings.widgetOffsetY");
        LblPinTaskbar.Text = Strings.Get("settings.pinTaskbar");


        LblInterval.Text = Strings.Get("settings.interval");
        LblIntervalHint.Text = Strings.Get("settings.intervalHint");
        LblStartup.Text = Strings.Get("settings.startWithWindows");
        LblClaudePrimeFive.Text = Strings.Get("settings.primeFive");
        LblClaudePrimeFiveHint.Text = Strings.Get("settings.claudePrimeFiveHint");
        LblClaudePrimeWeekly.Text = Strings.Get("settings.primeWeekly");
        LblClaudePrimeWeeklyHint.Text = Strings.Get("settings.claudePrimeWeeklyHint");
        LblCodexPrimeFive.Text = Strings.Get("settings.primeFive");
        LblCodexPrimeFiveHint.Text = Strings.Get("settings.codexPrimeFiveHint");
        LblCodexPrimeWeekly.Text = Strings.Get("settings.primeWeekly");
        LblCodexPrimeWeeklyHint.Text = Strings.Get("settings.codexPrimeWeeklyHint");

        LblSectionSystem.Text = Strings.Get("settings.sectionSystem");

        LblVersion.Text = Strings.Get("settings.version");
        LblAutoUpdate.Text = Strings.Get("settings.autoUpdate");
        VersionText.Text = AppInfo.Version;

        LblInstallPath.Text = Strings.Get("settings.installPath");
        OpenFolderButton.Content = Strings.Get("settings.openFolder");

        LblResetAll.Text = Strings.Get("settings.resetAll");
        LblResetAllHint.Text = Strings.Get("settings.resetAllHint");
        ResetAllButton.Content = Strings.Get("settings.resetAllButton");

        LblIssues.Text = Strings.Get("settings.issues");
        LblIssuesHint.Text = Strings.Get("settings.issuesHint");
        IssuesLink.Content = Strings.Get("settings.issuesButton");
        LblGitHub.Text = Strings.Get("settings.github");
        LblGitHubHint.Text = Strings.Get("settings.githubHint");
        GitHubLink.Content = Strings.Get("settings.githubButton");


        // 런처 유무는 문구가 갈리므로 상태를 다시 읽어 채운다.
        ShowInstallState();
        ShowUpdateState(_updates?.Last);

        RelabelIntervals();

        TaskbarHint.Text = !TaskbarPromotion.IsSupported
            ? Strings.Get("settings.pinUnsupported")
            : TaskbarPromotion.IsPromoted() is null
                ? Strings.Get("settings.pinPending")
                : Strings.Get("settings.pinTaskbarHint");
    }

    /// <summary>주기 세그먼트의 글자를 현재 언어로.</summary>
    private void RelabelIntervals()
    {
        foreach (RadioButton chip in IntervalGroup.Children)
            chip.Content = IntervalLabel((int)chip.Tag);
    }

    /// <summary>0은 자동 갱신 없음, 나머지는 분 단위로.</summary>
    private static string IntervalLabel(int seconds) =>
        seconds == 0
            ? Strings.Get("settings.intervalManual")
            : Strings.Get("age.minutes", seconds / 60);

    /// <summary>새로고침 주기를 세그먼트 버튼으로 만든다.</summary>
    private void BuildIntervalSegments()
    {
        foreach (int seconds in Intervals)
        {
            var chip = new RadioButton
            {
                Content = IntervalLabel(seconds),
                GroupName = "Interval",
                Style = (Style)Resources["SegmentTight"],
                Tag = seconds,
                IsChecked = seconds == _settings.RefreshIntervalSeconds,
            };

            chip.Checked += OnSettingChanged;
            IntervalGroup.Children.Add(chip);
        }

        // 저장된 값이 목록에 없으면 기본값을 고른다.
        if (SelectedInterval() < 0)
        {
            foreach (RadioButton chip in IntervalGroup.Children)
            {
                if ((int)chip.Tag != 600) continue;
                chip.IsChecked = true;
                break;
            }
        }
    }

    /// <summary>고른 주기(초). 아무것도 안 골랐으면 -1.</summary>
    private int SelectedInterval()
    {
        foreach (RadioButton chip in IntervalGroup.Children)
            if (chip.IsChecked == true) return (int)chip.Tag;

        return -1;
    }

    /// <summary>경로 입력을 펴고 접는다.</summary>
    private void OnPathToggle(object sender, RoutedEventArgs e) =>
        PathPanel.Visibility = PathToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>경로가 실제로 존재하는지 즉시 알려준다. 저장 전에 확인할 수 있게.</summary>
    private void UpdatePathStatus()
    {
        bool claudeOk = File.Exists(ClaudePath.Text);
        ClaudeStatus.Text = Strings.Get(claudeOk
            ? "settings.claudeFound"
            : "settings.claudeMissing");
        ClaudeDot.Background = StatusDot(claudeOk);

        bool codexOk = Directory.Exists(CodexPath.Text);
        string extra = "";
        if (codexOk)
        {
            try
            {
                int count = Directory.EnumerateFiles(CodexPath.Text, "rollout-*.jsonl",
                    SearchOption.AllDirectories).Take(1).Count();
                extra = Strings.Get(count > 0 ? "settings.codexFound" : "settings.codexEmpty");
            }
            catch { /* 접근 불가는 아래 메시지로 충분하다 */ }
        }

        CodexStatus.Text = codexOk ? extra : Strings.Get("settings.codexMissing");
        CodexDot.Background = StatusDot(codexOk);
    }

    /// <summary>준비됨이면 초록, 아니면 회색.</summary>
    private static Brush StatusDot(bool ready) => new SolidColorBrush(
        ready ? Color.FromRgb(0x3F, 0xB9, 0x50) : Color.FromRgb(0x9A, 0x9A, 0x9A));

    /// <summary>
    /// 컨트롤이 바뀔 때마다 바로 저장하고 앱에 반영한다.
    /// 저장 버튼이 없으므로 사용자가 따로 확정할 필요가 없다.
    /// </summary>
    private void OnSettingChanged(object sender, RoutedEventArgs e) => ApplyNow();

    /// <summary>경로는 다 입력한 뒤에 반영한다. 글자마다 저장할 이유가 없다.</summary>
    private void OnPathChanged(object sender, RoutedEventArgs e)
    {
        UpdatePathStatus();
        ApplyNow();
    }

    /// <summary>화면의 값을 설정에 담고 파일에 쓴 뒤 앱에 알린다.</summary>
    private void ApplyNow()
    {
        // 화면을 채우는 중에 들어온 이벤트는 무시한다. 아직 사용자 조작이 아니다.
        if (_loading) return;

        _settings.ClaudeEnabled = ClaudeEnabled.IsChecked == true;
        _settings.CodexEnabled = CodexEnabled.IsChecked == true;

        // 기본 경로와 같으면 빈 값으로 둔다. 나중에 기본이 바뀌어도 따라간다.
        _settings.ClaudeCredentialsPath =
            PathEquals(ClaudePath.Text, AppSettings.DefaultClaudePath) ? "" : ClaudePath.Text.Trim();
        _settings.CodexSessionsPath =
            PathEquals(CodexPath.Text, AppSettings.DefaultCodexPath) ? "" : CodexPath.Text.Trim();

        _settings.RefreshIntervalSeconds = SelectedInterval();

        _settings.DisplayMode = ModeUsed.IsChecked == true
            ? DisplayMode.Used
            : DisplayMode.Remaining;

        int langIdx = LanguageBox.SelectedIndex;
        if (langIdx >= 0 && langIdx < Strings.Languages.Length)
            _settings.Language = Strings.Languages[langIdx].Code;

        _settings.ClaudeColor = _claudeColor;
        _settings.CodexColor = _codexColor;

        bool autoStart = StartWithWindows.IsChecked == true;
        _settings.StartWithWindows = autoStart;
        _settings.ClaudePrimeFiveHour = ClaudePrimeFive.IsChecked == true;
        _settings.ClaudePrimeWeekly = ClaudePrimeWeekly.IsChecked == true;
        _settings.CodexPrimeFiveHour = CodexPrimeFive.IsChecked == true;
        _settings.CodexPrimeWeekly = CodexPrimeWeekly.IsChecked == true;
        _settings.AutoUpdate = AutoUpdateEnabled.IsChecked == true;
        ApplyAutoStart(autoStart);

        bool pin = PinToTaskbar.IsChecked == true;
        _settings.ShowInTaskbar = pin;
        _settings.ShowWidgetBar = ShowWidgetBar.IsChecked == true;
        int monitorIdx = WidgetMonitorBox.SelectedIndex;
        _settings.WidgetMonitorDeviceName = monitorIdx >= 0 && monitorIdx < _widgetScreens.Length
            ? _widgetScreens[monitorIdx].DeviceName
            : "";

        _settings.WidgetAutoOffset = WidgetAutoOffset.IsChecked == true;
        _settings.WidgetOffsetX = ParseOffset(WidgetOffsetX.Text);
        _settings.WidgetOffsetY = ParseOffset(WidgetOffsetY.Text);

        if (TaskbarPromotion.IsSupported) TaskbarPromotion.SetPromoted(pin);

        _settings.SetupCompleted = true;

        // 저장 실패는 조용히 넘기지 않는다. 다만 창을 막지는 않는다.
        if (_settings.Save() is { } error)
        {
            MessageBox.Show(this,
                Strings.Get("dialog.saveFailed", Environment.NewLine, error),
                Strings.Get("dialog.saveFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Saved?.Invoke();
    }

    /// <summary>
    /// 모든 설정을 기본값으로 되돌린다. 되돌릴 수 없으므로 먼저 확인을 받는다.
    /// 즉시 적용 방식이라 확인을 누르면 바로 저장된다.
    /// </summary>
    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            Strings.Get("dialog.resetAllBody", Environment.NewLine),
            Strings.Get("dialog.resetAllTitle"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        _settings.ResetAll();

        Strings.Current = _settings.Language;
        ReloadFromSettings();
        ApplyNow();
    }

    /// <summary>팝업에서 위젯바를 껐다 켰을 때 이 창의 토글도 따라가게 한다.</summary>
    public void SyncWidgetBarToggle()
    {
        // 편집 중인 다른 값을 건드리지 않으려고 이 토글만 갱신한다.
        // ReloadFromSettings()는 화면 전체를 되돌려 입력하던 내용을 지운다.
        bool wasLoading = _loading;
        _loading = true;
        ShowWidgetBar.IsChecked = _settings.ShowWidgetBar;
        _loading = wasLoading;
    }

    /// <summary>설정에 담긴 값으로 화면을 다시 그린다.</summary>
    private void ReloadFromSettings()
    {
        _loading = true;

        ClaudeEnabled.IsChecked = _settings.ClaudeEnabled;
        CodexEnabled.IsChecked = _settings.CodexEnabled;
        ClaudePath.Text = _settings.EffectiveClaudePath;
        CodexPath.Text = _settings.EffectiveCodexPath;
        StartWithWindows.IsChecked = _settings.StartWithWindows;
        ClaudePrimeFive.IsChecked = _settings.ClaudePrimeFiveHour;
        ClaudePrimeWeekly.IsChecked = _settings.ClaudePrimeWeekly;
        CodexPrimeFive.IsChecked = _settings.CodexPrimeFiveHour;
        CodexPrimeWeekly.IsChecked = _settings.CodexPrimeWeekly;
        AutoUpdateEnabled.IsChecked = _settings.AutoUpdate;
        PinToTaskbar.IsChecked = _settings.ShowInTaskbar;
        ShowWidgetBar.IsChecked = _settings.ShowWidgetBar;
        WidgetAutoOffset.IsChecked = _settings.WidgetAutoOffset;
        WidgetOffsetX.Text = _settings.WidgetOffsetX.ToString();
        WidgetOffsetY.Text = _settings.WidgetOffsetY.ToString();
        ShowOffsetRow();
        BuildWidgetMonitorList();

        if (_settings.DisplayMode == DisplayMode.Used) ModeUsed.IsChecked = true;
        else ModeRemaining.IsChecked = true;

        foreach (RadioButton chip in IntervalGroup.Children)
            chip.IsChecked = (int)chip.Tag == _settings.RefreshIntervalSeconds;

        _claudeColor = _settings.ClaudeColor;
        _codexColor = _settings.CodexColor;
        UpdateSwatches();

        int idx = Array.FindIndex(Strings.Languages,
            l => string.Equals(l.Code, _settings.Language, StringComparison.OrdinalIgnoreCase));
        LanguageBox.SelectedIndex = idx >= 0 ? idx : 0;

        Retranslate();
        UpdatePathStatus();

        _loading = false;
    }


    // ---- 색상 선택 ----

    private void OnPickClaudeColor(object sender, RoutedEventArgs e)
    {
        if (PickColor(ClaudeSwatch, "Claude", _claudeColor,
                      AppSettings.DefaultClaudeColor) is { } picked)
        {
            _claudeColor = picked;
            UpdateSwatches();
            ApplyNow();
        }
    }

    private void OnPickCodexColor(object sender, RoutedEventArgs e)
    {
        if (PickColor(CodexSwatch, "Codex", _codexColor,
                      AppSettings.DefaultCodexColor) is { } picked)
        {
            _codexColor = picked;
            UpdateSwatches();
            ApplyNow();
        }
    }

    /// <summary>견본 아래에 팔레트를 띄운다. 고르지 않고 닫으면 null.</summary>
    private string? PickColor(FrameworkElement anchor, string toolName,
                              string current, string fallback)
    {
        var picker = new ColorPickerWindow(
            Strings.Get("color.title", toolName), current, fallback);

        picker.ShowUnder(anchor, this);

        // ShowUnder는 모달이 아니므로 닫힐 때까지 기다린다.
        var frame = new System.Windows.Threading.DispatcherFrame();
        picker.Closed += (_, _) => frame.Continue = false;
        System.Windows.Threading.Dispatcher.PushFrame(frame);

        return picker.Selected;
    }

    private void UpdateSwatches()
    {
        ClaudeSwatch.Background = ToBrush(_claudeColor);
        CodexSwatch.Background = ToBrush(_codexColor);
    }

    private static System.Windows.Media.Brush ToBrush(string hex)
    {
        try
        {
            var c = (System.Windows.Media.Color)
                System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return new System.Windows.Media.SolidColorBrush(c);
        }
        catch
        {
            return System.Windows.Media.Brushes.Gray;
        }
    }

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
            return System.Drawing.Color.Gray;
        }
    }

    // ---- 경로 선택 ----

    private void OnBrowseClaude(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.Get("dialog.pickClaude"),
            Filter = Strings.Get("dialog.credentialFilter"),
            InitialDirectory = SafeDirectory(ClaudePath.Text),
        };

        if (dlg.ShowDialog(this) == true)
        {
            ClaudePath.Text = dlg.FileName;
            UpdatePathStatus();
        }
    }

    private void OnBrowseCodex(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Strings.Get("dialog.pickCodex"),
            InitialDirectory = SafeDirectory(CodexPath.Text),
        };

        if (dlg.ShowDialog(this) == true)
        {
            CodexPath.Text = dlg.FolderName;
            UpdatePathStatus();
        }
    }

    /// <summary>대화상자 시작 위치. 없는 경로를 넘기면 무시되므로 안전하게 고른다.</summary>
    private static string SafeDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) return path;
            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return parent;
        }
        catch { /* 잘못된 경로 문자 등 */ }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a.Trim()).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ---- 자동 실행 ----

    private static void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enable)
            {
                // 런처로 설치된 환경은 Windows 시작 때 런처가 업데이트를 먼저
                // 적용한 뒤 앱을 연다. 개발/수동 실행 환경만 현재 exe를 등록한다.
                string exe = File.Exists(AppSettings.LauncherPath)
                    ? AppSettings.LauncherPath
                    : Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 정책으로 막힌 환경도 있다. 설정 저장 자체는 계속 진행한다.
        }
    }
}
