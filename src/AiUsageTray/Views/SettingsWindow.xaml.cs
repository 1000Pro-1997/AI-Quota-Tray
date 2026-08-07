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

    /// <summary>고를 수 있는 새로고침 주기(초). 0은 자동 갱신 없음.</summary>
    private static readonly int[] Intervals = { 60, 300, 600, 1800, 0 };

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

        VersionText.Text = $"{AppInfo.Name} {AppInfo.Version}";

        UpdatePathStatus();
    }

    private void OnOpenIssues(object sender, RoutedEventArgs e) => App.OpenIssues();

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
        UpdatePathStatus();
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
        LblPinTaskbar.Text = Strings.Get("settings.pinTaskbar");

        LblBarColors.Text = Strings.Get("settings.barColors");
        LblBarColorsHint.Text = Strings.Get("settings.barColorsHint");
        ResetColors.Content = Strings.Get("settings.resetColors");

        LblInterval.Text = Strings.Get("settings.interval");
        LblIntervalHint.Text = Strings.Get("settings.intervalHint");
        LblStartup.Text = Strings.Get("settings.startWithWindows");

        TestButton.Content = Strings.Get("settings.test");
        CancelButton.Content = Strings.Get("settings.cancel");
        SaveButton.Content = Strings.Get("settings.save");
        IssuesLink.Content = Strings.Get("settings.issues");

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

        int langIdx = LanguageBox.SelectedIndex;
        if (langIdx >= 0 && langIdx < Strings.Languages.Length)
            _settings.Language = Strings.Languages[langIdx].Code;

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
                Strings.Get("dialog.pinFailed", Environment.NewLine, pinError),
                Strings.Get("dialog.notice"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _settings.SetupCompleted = true;

        if (_settings.Save() is { } error)
        {
            MessageBox.Show(this,
                Strings.Get("dialog.saveFailed", Environment.NewLine, error),
                Strings.Get("dialog.saveFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return; // 창을 닫지 않아 사용자가 다시 시도할 수 있게 한다.
        }

        Saved?.Invoke();
        Close();
    }

    /// <summary>
    /// 모든 설정을 기본값으로 되돌린다. 되돌릴 수 없으므로 먼저 확인을 받는다.
    /// 화면만 바꾸고 파일에는 쓰지 않는다. 저장을 눌러야 확정된다.
    /// </summary>
    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            Strings.Get("dialog.resetAllBody", Environment.NewLine),
            Strings.Get("dialog.resetAllTitle"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        _settings.ResetAll();

        // 바뀐 값으로 화면을 다시 채운다.
        Strings.Current = _settings.Language;
        ReloadFromSettings();
    }

    /// <summary>저장된 값으로 화면을 다시 그린다.</summary>
    private void ReloadFromSettings()
    {
        ClaudeEnabled.IsChecked = _settings.ClaudeEnabled;
        CodexEnabled.IsChecked = _settings.CodexEnabled;
        ClaudePath.Text = _settings.EffectiveClaudePath;
        CodexPath.Text = _settings.EffectiveCodexPath;
        StartWithWindows.IsChecked = _settings.StartWithWindows;
        PinToTaskbar.IsChecked = _settings.ShowInTaskbar;
        ShowWidgetBar.IsChecked = _settings.ShowWidgetBar;

        if (_settings.DisplayMode == DisplayMode.Used) ModeUsed.IsChecked = true;
        else ModeRemaining.IsChecked = true;

        foreach (RadioButton chip in IntervalGroup.Children)
            chip.IsChecked = (int)chip.Tag == _settings.RefreshIntervalSeconds;

        _claudeColor = _settings.ClaudeColor;
        _codexColor = _settings.CodexColor;
        UpdateSwatches();

        _loadingLanguages = true;
        int idx = Array.FindIndex(Strings.Languages,
            l => string.Equals(l.Code, _settings.Language, StringComparison.OrdinalIgnoreCase));
        LanguageBox.SelectedIndex = idx >= 0 ? idx : 0;
        _loadingLanguages = false;

        Retranslate();
        UpdatePathStatus();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // 미리보기로 바꿔둔 언어를 저장된 값으로 되돌린다.
        Strings.Current = _settings.Language;
        Close();
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        UpdatePathStatus();

        TestButton.IsEnabled = false;
        string original = (string)TestButton.Content;
        TestButton.Content = Strings.Get("popup.checking");

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
                ? $"✓ {u.Provider}: {string.Join(", ", u.Windows.Select(w => w.Label + " " + Strings.Get("value.used", $"{w.Percent:F0}")))}"
                : $"✗ {u.Provider}: {u.Error}");

            string message = monitor.Latest.Count == 0
                ? Strings.Get("dialog.testNoTools")
                : string.Join(Environment.NewLine, lines);

            MessageBox.Show(this, message, Strings.Get("dialog.testTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Strings.Get("dialog.testTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
