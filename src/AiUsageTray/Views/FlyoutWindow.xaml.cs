using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AiUsageTray.Models;
using AiUsageTray.Services;

namespace AiUsageTray.Views;

public partial class FlyoutWindow : Window
{
    private const string ThemeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>남은 양으로 볼지, 쓴 양으로 볼지. 설정에서 바뀐다.</summary>
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Remaining;
    public Func<UsageWindow, string>? TimeFormatter { get; set; }
    public Func<UsageWindow, bool>? SecondDisplayResolver { get; set; }

    /// <summary>공급자 이름 → 진행률 바 색(#RRGGBB)을 돌려준다.</summary>
    public Func<string, string>? ColorResolver { get; set; }

    /// <summary>공급자 이름 → 서비스 장애 상태.</summary>
    public Func<string, ServiceStatus>? StatusResolver { get; set; }

    /// <summary>마지막으로 숨겨진 시각. 클릭 한 번이 닫고 다시 여는 것을 막는 데 쓴다.</summary>
    public DateTime HiddenAt { get; private set; } = DateTime.MinValue;

    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? WidgetBarToggled;

    public bool WidgetBarEnabled
    {
        get => WidgetBarToggle.IsChecked == true;
        set => WidgetBarToggle.IsChecked = value;
    }

    public FlyoutWindow()
    {
        InitializeComponent();
        ApplyTheme();
        Retranslate();
        Deactivated += (_, _) => Hide();

        // 창이 떠 있는 동안에만 "몇 초 전"을 세어 준다. 닫혀 있으면 셀 이유가 없다.
        _ageTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ageTicker.Tick += (_, _) =>
        {
            UpdateRefreshedText();
            UpdateCountdowns();
        };

        IsVisibleChanged += (_, e) =>
        {
            bool shown = (bool)e.NewValue;
            if (!shown) HiddenAt = DateTime.Now;

            if (shown)
            {
                UpdateRefreshedText();
                _ageTicker.Start();
            }
            else
            {
                _ageTicker.Stop();
            }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    private readonly DispatcherTimer _ageTicker;
    private readonly List<(TextBlock Label, UsageWindow Window)> _timeLabels = new();
    private DateTime _lastCountdownUpdate = DateTime.MinValue;

    // ---- 테마 ----

    /// <summary>Windows 앱 테마(밝게/어둡게)를 읽어 색을 맞춘다.</summary>
    private void ApplyTheme()
    {
        bool dark = IsSystemDarkTheme();

        void Set(string key, Color c) => Resources[key] = new SolidColorBrush(c);

        // 설정 창과 같은 팔레트를 쓴다. 두 창이 한 앱처럼 보여야 한다.
        if (dark)
        {
            Set("WindowBrush", Color.FromRgb(0x1C, 0x1C, 0x1C));
            Set("CardBrush", Color.FromRgb(0x27, 0x27, 0x27));
            Set("BorderBrush2", Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
            Set("TextBrush", Color.FromRgb(0xF2, 0xF2, 0xF2));
            Set("SubtleBrush", Color.FromRgb(0x94, 0x94, 0x94));
            Set("TrackBrush", Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            Set("TrackOffBrush", Color.FromArgb(0x32, 0xFF, 0xFF, 0xFF));
            Set("AccentBrush", Color.FromRgb(0x4C, 0x8E, 0xF0));
            Set("HoverBrush", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            Set("WindowBrush", Color.FromRgb(0xF5, 0xF5, 0xF7));
            Set("CardBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            Set("BorderBrush2", Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
            Set("TextBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            Set("SubtleBrush", Color.FromRgb(0x70, 0x70, 0x74));
            Set("TrackBrush", Color.FromArgb(0x18, 0x00, 0x00, 0x00));
            Set("TrackOffBrush", Color.FromArgb(0x24, 0x00, 0x00, 0x00));
            Set("AccentBrush", Color.FromRgb(0x2F, 0x7C, 0xEA));
            Set("HoverBrush", Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        }
    }

    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ThemeKeyPath);
            // AppsUseLightTheme: 0이면 다크
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return true; // 못 읽으면 다크로. 트레이 주변은 보통 어둡다.
        }
    }

    // ---- 내용 갱신 ----

    public void Render(IReadOnlyList<ProviderUsage> usages)
    {
        ProviderList.Items.Clear();
        _timeLabels.Clear();

        if (usages.Count == 0)
        {
            ProviderList.Items.Add(BuildMessage(Strings.Get("popup.nothing")));
            return;
        }

        bool first = true;
        foreach (var u in usages)
        {
            ProviderList.Items.Add(WrapInCard(BuildProviderCard(u), first));
            first = false;
        }
    }

    /// <summary>도구 하나를 카드로 감싼다. 설정 창의 카드와 같은 모양이다.</summary>
    private UIElement WrapInCard(UIElement content, bool first) => new Border
    {
        Background = (Brush)Resources["CardBrush"],
        BorderBrush = (Brush)Resources["BorderBrush2"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(13, 11, 13, 12),
        Margin = new Thickness(0, first ? 0 : 8, 0, 0),
        Child = content,
    };

    /// <summary>언어가 바뀌면 고정 문구를 새 말로 바꾼다.</summary>
    public void Retranslate()
    {
        TitleText.Text = Strings.Get("app.name");
        RefreshButton.ToolTip = Strings.Get("tip.refresh");
        SettingsButton.ToolTip = Strings.Get("tip.settings");
    }

    public void SetBusy(bool busy)
    {
        StatusText.Text = busy ? Strings.Get("popup.checking") : "";

        if (busy) StartSpin();
        else StopSpin();

        // 조회가 끝나면 "방금 전"으로 바로 바뀌어야 한다.
        if (!busy) UpdateRefreshedText();
    }

    // ---- 새로고침 시각 ----

    /// <summary>마지막으로 새로고침한 시각을 준다. 아직 없으면 null.</summary>
    public Func<DateTime?>? RefreshedAtResolver { get; set; }

    /// <summary>
    /// "몇 분 전 새로고침"을 다시 그린다.
    ///
    /// 창이 떠 있는 동안에도 시간은 흐르므로 타이머로 계속 고쳐 쓴다.
    /// 조회 중에는 숨긴다. 옆에서 스피너가 도는데 시각까지 있으면 시끄럽다.
    /// </summary>
    private void UpdateRefreshedText()
    {
        var at = RefreshedAtResolver?.Invoke();

        if (at is null || _spinning)
        {
            RefreshedText.Visibility = Visibility.Collapsed;
            return;
        }

        RefreshedText.Text = Strings.Get("popup.refreshed", FormatAge(DateTime.Now - at.Value));
        RefreshedText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 조회하는 동안 새로고침 아이콘을 돌린다.
    ///
    /// BeginAnimation이 걸린 동안에는 Angle을 읽어도 현재 각도가 아니라 기본값이
    /// 나온다. 그래서 "지금 각도에서 마저 돌기" 같은 계산은 할 수 없다. 대신
    /// 한 바퀴가 끝나는 지점에서만 멈추도록 반복 횟수를 다시 건다.
    /// </summary>
    private void StartSpin()
    {
        if (_spinning) return;
        _spinning = true;
        _spinStarted = DateTime.Now;

        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = SpinCycle,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        RefreshSpin.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    /// <summary>
    /// 조회가 끝나면 멈춘다. 도는 중간에 뚝 끊기면 어색하므로,
    /// 지금 돌던 바퀴를 끝까지 채우고 나서 선다.
    /// </summary>
    private void StopSpin()
    {
        if (!_spinning) return;
        _spinning = false;

        // 시작 시각을 알고 있으니 이번 바퀴가 언제 끝나는지 계산할 수 있다.
        var elapsed = DateTime.Now - _spinStarted;
        double intoCycle = elapsed.TotalMilliseconds % SpinCycle.TotalMilliseconds;
        var remaining = TimeSpan.FromMilliseconds(SpinCycle.TotalMilliseconds - intoCycle);

        var finish = new DoubleAnimation
        {
            // 남은 만큼만 돌아 360에서 정확히 멈춘다.
            From = 360 - (remaining.TotalMilliseconds / SpinCycle.TotalMilliseconds * 360),
            To = 360,
            Duration = remaining,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        finish.Completed += (_, _) =>
        {
            // 애니메이션을 떼고 각도를 처음으로 되돌린다.
            RefreshSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            RefreshSpin.Angle = 0;
        };

        RefreshSpin.BeginAnimation(RotateTransform.AngleProperty, finish);
    }

    private static readonly TimeSpan SpinCycle = TimeSpan.FromMilliseconds(800);
    private bool _spinning;
    private DateTime _spinStarted;

    private UIElement BuildMessage(string text) => new TextBlock
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(0, 4, 0, 8),
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Resources["SubtleBrush"],
    };

    private UIElement BuildProviderCard(ProviderUsage u)
    {
        var panel = new StackPanel();

        // 제목 줄: 이름 + 서비스 상태 | 요금제
        var header = new Grid();

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock
        {
            Text = u.Provider,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Resources["TextBrush"],
        });

        // 서비스 장애 상태를 점과 말로 알린다.
        var status = StatusResolver?.Invoke(u.Provider);
        if (status is not null && status.Health != ServiceHealth.Unknown)
        {
            var dotColor = ParseColor(status.Color);

            left.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = new SolidColorBrush(dotColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 1, 4, 0),
            });

            var statusText = new TextBlock
            {
                Text = status.Label,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),

                // 정상일 때는 조용히, 문제가 있을 때만 색으로 눈에 띄게.
                Foreground = status.NeedsAttention
                    ? new SolidColorBrush(dotColor)
                    : (Brush)Resources["SubtleBrush"],
            };

            // 상태를 누르면 그 서비스의 상태 페이지를 연다. 여기 뜬 한 줄만으로는
            // 무엇이 얼마나 고장났는지 알 수 없어서, 자세히 볼 곳으로 보내준다.
            string? page = StatusProvider.PageFor(u.Provider);
            if (page is not null)
            {
                statusText.Cursor = Cursors.Hand;
                statusText.ToolTip = page;

                // 밑줄은 누를 수 있다는 표시다. 평소에는 조용히 두고
                // 마우스를 올렸을 때만 보여준다.
                statusText.MouseEnter += (_, _) => statusText.TextDecorations = TextDecorations.Underline;
                statusText.MouseLeave += (_, _) => statusText.TextDecorations = null;

                statusText.MouseLeftButtonUp += (_, e) =>
                {
                    // 팝업 전체의 클릭 처리로 번지지 않게 한다.
                    e.Handled = true;
                    OpenLink(page);
                };
            }

            left.Children.Add(statusText);
        }

        header.Children.Add(left);

        if (!string.IsNullOrEmpty(u.PlanName))
        {
            header.Children.Add(new TextBlock
            {
                Text = u.PlanName,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Resources["SubtleBrush"],
            });
        }
        panel.Children.Add(header);

        if (!u.IsAvailable)
        {
            panel.Children.Add(new TextBlock
            {
                Text = u.Error,
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Resources["SubtleBrush"],
            });
            return panel;
        }

        var barBrush = ResolveBrush(u.Provider);
        foreach (var w in u.Windows)
            panel.Children.Add(BuildWindowRow(w, barBrush));

        // 갱신하지 못한 이유가 있으면 수치 아래에 덧붙인다.
        if (u.IsStale && u.Error is { } why)
        {
            panel.Children.Add(new TextBlock
            {
                Text = why,
                FontSize = 10.5,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Resources["SubtleBrush"],
            });
        }

        return panel;
    }

    /// <summary>
    /// 바 색은 도구를 구분하는 용도라 사용률과 무관하게 고정이다.
    /// 대신 여유가 얼마 없을 때는 숫자 옆에 배지를 붙여 알린다.
    /// </summary>
    private UIElement BuildWindowRow(UsageWindow w, Brush barBrush)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };

        double used = Math.Clamp(w.Percent, 0, 100);
        bool remaining = DisplayMode == DisplayMode.Remaining;

        // 잔여량 모드에서는 바도 남은 만큼 찬다. 줄어드는 게 눈에 보이게.
        double barValue = remaining ? 100 - used : used;
        string valueText = remaining
            ? Strings.Get("value.remaining", $"{100 - used:F0}")
            : Strings.Get("value.used", $"{used:F0}");

        var line = new Grid();
        line.Children.Add(new TextBlock
        {
            Text = w.Label,
            FontSize = 11.5,
            Foreground = (Brush)Resources["TextBrush"],
        });

        // 진행률 바와 숫자가 이미 상태를 말해준다. 배지는 덧붙이지 않는다.
        line.Children.Add(new TextBlock
        {
            Text = valueText,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Resources["TextBrush"],
        });
        wrap.Children.Add(line);

        wrap.Children.Add(new ProgressBar
        {
            Style = (Style)Resources["UsageBar"],
            Value = barValue,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = barBrush,
        });

        string resetText = TimeFormatter?.Invoke(w) ?? w.ResetText;
        if (!string.IsNullOrEmpty(resetText))
        {
            var timeLabel = new TextBlock
            {
                Text = resetText,
                FontSize = 10.5,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = (Brush)Resources["SubtleBrush"],
            };
            _timeLabels.Add((timeLabel, w));
            wrap.Children.Add(timeLabel);
        }

        return wrap;
    }

    private void UpdateCountdowns()
    {
        bool showsSeconds = _timeLabels.Any(x => SecondDisplayResolver?.Invoke(x.Window) == true);
        if (!showsSeconds && DateTime.Now - _lastCountdownUpdate < TimeSpan.FromSeconds(30)) return;
        _lastCountdownUpdate = DateTime.Now;
        foreach (var (label, window) in _timeLabels)
            label.Text = TimeFormatter?.Invoke(window) ?? window.ResetText;
    }

    /// <summary>설정에서 받은 #RRGGBB를 브러시로. 잘못된 값이면 기본 회색.</summary>
    private Brush ResolveBrush(string provider)
    {
        string hex = ColorResolver?.Invoke(provider) ?? "#8B8B8B";
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(c);
        }
        catch
        {
            return new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0x8B));
        }
    }

    /// <summary>
    /// 기본 브라우저로 주소를 연다.
    ///
    /// 열지 못해도 알리지 않는다. 사용량 확인이라는 본래 일은 그대로 되고,
    /// 팝업은 포커스를 잃으면 닫히는 창이라 여기서 오류를 띄울 자리가 없다.
    /// </summary>
    private static void OpenLink(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }

    /// <summary>#RRGGBB 문자열을 색으로. 잘못된 값이면 회색.</summary>
    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Color.FromRgb(0x8B, 0x8B, 0x8B);
        }
    }

    private static Brush ToBrush(System.Drawing.Color c) =>
        new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));

    /// <summary>
    /// 경과 시간을 가장 큰 단위 하나로만 적는다. 1분이 안 됐으면 초 단위로.
    /// 시계가 뒤로 갔거나 하는 이유로 음수가 나오면 0초로 본다.
    /// </summary>
    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;

        return age.TotalDays >= 1 ? Strings.Get("age.days", (int)age.TotalDays) :
               age.TotalHours >= 1 ? Strings.Get("age.hours", (int)age.TotalHours) :
               age.TotalMinutes >= 1 ? Strings.Get("age.minutes", (int)age.TotalMinutes) :
               Strings.Get("age.seconds", (int)age.TotalSeconds);
    }

    // ---- 위치 계산 ----

    /// <summary>
    /// 커서(트레이 아이콘) 근처, 작업표시줄을 피해서 띄운다.
    /// 작업표시줄이 어느 변에 있든 화면 안쪽으로 배치한다.
    /// </summary>
    public void ShowNearTray()
    {
        // 보이지 않는 상태로 먼저 띄워 실제 크기를 확정한다.
        Opacity = 0;
        Show();
        UpdateLayout();

        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var work = screen.WorkingArea;
        var full = screen.Bounds;

        double scale = GetDpiScale();
        double pw = ActualWidth * scale;
        double ph = ActualHeight * scale;
        const int Gap = 4;

        double x, y;

        if (work.Bottom < full.Bottom)          // 작업표시줄: 아래
        {
            x = cursor.X - pw / 2;
            y = work.Bottom - ph + Gap;
        }
        else if (work.Top > full.Top)           // 위
        {
            x = cursor.X - pw / 2;
            y = work.Top - Gap;
        }
        else if (work.Left > full.Left)         // 왼쪽
        {
            x = work.Left - Gap;
            y = cursor.Y - ph / 2;
        }
        else if (work.Right < full.Right)       // 오른쪽
        {
            x = work.Right - pw + Gap;
            y = cursor.Y - ph / 2;
        }
        else                                    // 자동 숨김 등
        {
            x = cursor.X - pw / 2;
            y = work.Bottom - ph;
        }

        // 화면 밖으로 나가지 않게 가둔다.
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - pw));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - ph));

        Left = x / scale;
        Top = y / scale;
        Opacity = 1;

        Activate();
        Focus();
    }

    private double GetDpiScale()
    {
        var src = PresentationSource.FromVisual(this);
        return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    // ---- 이벤트 ----

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void OnWidgetBarToggleClick(object sender, RoutedEventArgs e) =>
        WidgetBarToggled?.Invoke(WidgetBarEnabled);

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        SettingsRequested?.Invoke();
    }

}
