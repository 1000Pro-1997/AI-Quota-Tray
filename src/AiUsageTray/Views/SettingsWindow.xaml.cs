using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

    private static readonly (string Label, int Seconds)[] Intervals =
    {
        ("1분", 60),
        ("5분", 300),
        ("10분", 600),
        ("30분", 1800),
        ("수동", 0),
    };

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
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

    private void LoadFromSettings()
    {
        ClaudeEnabled.IsChecked = _settings.ClaudeEnabled;
        CodexEnabled.IsChecked = _settings.CodexEnabled;

        // 비어 있으면 자동 탐지된 기본 경로를 보여준다. 사용자가 무엇을 읽는지 알 수 있게.
        ClaudePath.Text = _settings.EffectiveClaudePath;
        CodexPath.Text = _settings.EffectiveCodexPath;

        StartWithWindows.IsChecked = _settings.StartWithWindows;

        // 실제 레지스트리 상태를 우선한다. 사용자가 Windows 설정에서
        // 직접 바꿨을 수 있으므로 저장된 값보다 현재 상태가 정확하다.
        PinToTaskbar.IsChecked = TaskbarPromotion.IsPromoted() ?? _settings.ShowInTaskbar;
        ShowWidgetBar.IsChecked = _settings.ShowWidgetBar;

        if (!TaskbarPromotion.IsSupported)
        {
            PinToTaskbar.IsEnabled = false;
            TaskbarHint.Text = "이 Windows 버전에서는 지원되지 않습니다.";
        }
        else if (TaskbarPromotion.IsPromoted() is null)
        {
            TaskbarHint.Text = "트레이 아이콘이 준비되면 적용됩니다.";
        }

        BuildIntervalSegments();

        // 표기 모드는 두 칸짜리 세그먼트다.
        if (_settings.DisplayMode == DisplayMode.Used) ModeUsed.IsChecked = true;
        else ModeRemaining.IsChecked = true;

        _claudeColor = _settings.ClaudeColor;
        _codexColor = _settings.CodexColor;
        UpdateSwatches();

        VersionText.Text = $"{AppInfo.Name} {AppInfo.Version}";

        UpdatePathStatus();
    }

    private void OnOpenIssues(object sender, RoutedEventArgs e) => App.OpenIssues();

    /// <summary>새로고침 주기를 세그먼트 버튼으로 만든다.</summary>
    private void BuildIntervalSegments()
    {
        foreach (var (label, seconds) in Intervals)
        {
            var chip = new RadioButton
            {
                Content = label,
                GroupName = "Interval",
                Style = (Style)Resources["SegmentTight"],
                Tag = seconds,
                IsChecked = seconds == _settings.RefreshIntervalSeconds,
            };

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
        ClaudeStatus.Text = claudeOk
            ? "로그인 정보를 찾았습니다"
            : "파일을 찾을 수 없습니다. Claude Code에 로그인하세요";
        ClaudeDot.Background = StatusDot(claudeOk);

        bool codexOk = Directory.Exists(CodexPath.Text);
        string extra = "";
        if (codexOk)
        {
            try
            {
                int count = Directory.EnumerateFiles(CodexPath.Text, "rollout-*.jsonl",
                    SearchOption.AllDirectories).Take(1).Count();
                extra = count > 0 ? "세션 기록을 찾았습니다" : "세션 기록이 아직 없습니다";
            }
            catch { /* 접근 불가는 아래 메시지로 충분하다 */ }
        }

        CodexStatus.Text = codexOk
            ? extra
            : "폴더를 찾을 수 없습니다. Codex를 한 번 실행하세요";
        CodexDot.Background = StatusDot(codexOk);
    }

    /// <summary>준비됨이면 초록, 아니면 회색.</summary>
    private static Brush StatusDot(bool ready) => new SolidColorBrush(
        ready ? Color.FromRgb(0x3F, 0xB9, 0x50) : Color.FromRgb(0x9A, 0x9A, 0x9A));

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.ClaudeEnabled = ClaudeEnabled.IsChecked == true;
        _settings.CodexEnabled = CodexEnabled.IsChecked == true;

        // 기본 경로와 같으면 빈 값으로 저장한다. 그래야 나중에 기본이 바뀌어도 따라간다.
        _settings.ClaudeCredentialsPath =
            PathEquals(ClaudePath.Text, AppSettings.DefaultClaudePath) ? "" : ClaudePath.Text.Trim();
        _settings.CodexSessionsPath =
            PathEquals(CodexPath.Text, AppSettings.DefaultCodexPath) ? "" : CodexPath.Text.Trim();

        _settings.RefreshIntervalSeconds = SelectedInterval();

        _settings.DisplayMode = ModeUsed.IsChecked == true
            ? DisplayMode.Used
            : DisplayMode.Remaining;

        _settings.ClaudeColor = _claudeColor;
        _settings.CodexColor = _codexColor;

        bool autoStart = StartWithWindows.IsChecked == true;
        _settings.StartWithWindows = autoStart;
        ApplyAutoStart(autoStart);

        bool pin = PinToTaskbar.IsChecked == true;
        _settings.ShowInTaskbar = pin;
        _settings.ShowWidgetBar = ShowWidgetBar.IsChecked == true;

        if (TaskbarPromotion.IsSupported && TaskbarPromotion.SetPromoted(pin) is { } pinError)
        {
            MessageBox.Show(this,
                $"작업표시줄 표시를 바꾸지 못했습니다.{Environment.NewLine}{pinError}",
                "알림", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _settings.SetupCompleted = true;

        if (_settings.Save() is { } error)
        {
            MessageBox.Show(this,
                $"설정을 저장하지 못했습니다.{Environment.NewLine}{error}",
                "저장 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            return; // 창을 닫지 않아 사용자가 다시 시도할 수 있게 한다.
        }

        Saved?.Invoke();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        UpdatePathStatus();

        TestButton.IsEnabled = false;
        string original = (string)TestButton.Content;
        TestButton.Content = "확인 중…";

        try
        {
            // 화면의 현재 값으로 임시 설정을 만들어 실제 조회를 시도한다.
            var probe = new AppSettings
            {
                ClaudeEnabled = ClaudeEnabled.IsChecked == true,
                CodexEnabled = CodexEnabled.IsChecked == true,
                ClaudeCredentialsPath = ClaudePath.Text.Trim(),
                CodexSessionsPath = CodexPath.Text.Trim(),
            };

            using var monitor = new UsageMonitor(probe);
            await monitor.RefreshAsync();

            var lines = monitor.Latest.Select(u => u.Error is null
                ? $"✓ {u.Provider}: {string.Join(", ", u.Windows.Select(w => $"{w.Label} {w.Percent:F0}% 사용"))}"
                : $"✗ {u.Provider}: {u.Error}");

            string message = monitor.Latest.Count == 0
                ? "활성화된 도구가 없습니다."
                : string.Join(Environment.NewLine, lines);

            MessageBox.Show(this, message, "연결 테스트", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "연결 테스트", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            TestButton.Content = original;
            TestButton.IsEnabled = true;
        }
    }

    // ---- 색상 선택 ----

    private void OnPickClaudeColor(object sender, RoutedEventArgs e)
    {
        if (PickColor(_claudeColor) is { } picked)
        {
            _claudeColor = picked;
            UpdateSwatches();
        }
    }

    private void OnPickCodexColor(object sender, RoutedEventArgs e)
    {
        if (PickColor(_codexColor) is { } picked)
        {
            _codexColor = picked;
            UpdateSwatches();
        }
    }

    private void OnResetColors(object sender, RoutedEventArgs e)
    {
        _claudeColor = AppSettings.DefaultClaudeColor;
        _codexColor = AppSettings.DefaultCodexColor;
        UpdateSwatches();
    }

    /// <summary>Windows 기본 색 선택 대화상자. 취소하면 null.</summary>
    private static string? PickColor(string current)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = ToDrawingColor(current),
        };

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;

        var c = dlg.Color;
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
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
            Title = "Claude 자격증명 파일 선택",
            Filter = "자격증명 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
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
            Title = "Codex 세션 폴더 선택",
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
                string exe = Environment.ProcessPath ?? "";
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
