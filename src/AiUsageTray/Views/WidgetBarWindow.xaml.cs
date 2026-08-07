using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AiUsageTray.Models;
using AiUsageTray.Services;

namespace AiUsageTray.Views;

/// <summary>
/// 작업표시줄 옆에 붙어 사용량을 가로로 보여주는 작은 막대.
///
/// Windows 11에서는 작업표시줄에 직접 무언가를 넣는 공식 방법(Deskband)이
/// 막혀 있다. 그래서 항상 위에 뜨는 창을 알림 영역 왼쪽에 겹쳐 놓는 방식을 쓴다.
/// </summary>
public partial class WidgetBarWindow : Window
{
    private const string ThemeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // 창을 활성화하지 않고, 작업 전환기(Alt+Tab)에도 뜨지 않게 한다.
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);

    // 창을 topmost 목록의 맨 위로 다시 올리되, 위치·크기·활성화는 건드리지 않는다.
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    /// <summary>막대를 눌렀을 때. 팝업을 열어주면 된다.</summary>
    public event Action? Clicked;

    public DisplayMode DisplayMode { get; set; } = DisplayMode.Remaining;

    /// <summary>한 줄의 크기. 두 도구의 줄이 나란히 보이도록 고정한다.</summary>
    private const double RowWidth = 96;
    private const double RowHeight = 16;

    /// <summary>공급자 이름 → 색(#RRGGBB).</summary>
    public Func<string, string>? ColorResolver { get; set; }

    /// <summary>공급자 이름 → 서비스 장애 상태.</summary>
    public Func<string, ServiceStatus>? StatusResolver { get; set; }

    /// <summary>
    /// 작업표시줄도 topmost 창이라, 사용자가 여기저기 클릭하면 Windows가
    /// Z순서를 재배치하면서 이 창을 작업표시줄 뒤로 밀어버린다. 주기적으로
    /// 맨 위를 되찾아야 계속 보인다.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _keepOnTop = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };

    /// <summary>남은 시간이 줄어드는 것을 보여주려면 조회와 무관하게 다시 그려야 한다.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _tick = new()
    {
        Interval = TimeSpan.FromSeconds(30),
    };

    /// <summary>마지막으로 그린 값. 시간만 갱신할 때 다시 쓴다.</summary>
    private IReadOnlyList<ProviderUsage> _shown = Array.Empty<ProviderUsage>();

    public WidgetBarWindow()
    {
        InitializeComponent();
        ApplyTheme();

        SourceInitialized += (_, _) => HookWindow();

        // 마우스를 올리면 살짝 밝아져 누를 수 있다는 걸 알린다.
        Items.MouseEnter += (_, _) => Items.Opacity = 0.88;
        Items.MouseLeave += (_, _) => Items.Opacity = 1.0;

        _keepOnTop.Tick += (_, _) => BringToTop();

        // 사용량은 그대로여도 남은 시간은 계속 줄어든다.
        _tick.Tick += (_, _) => { if (_shown.Count > 0) Render(_shown); };

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { _keepOnTop.Start(); _tick.Start(); }
            else { _keepOnTop.Stop(); _tick.Stop(); }
        };

        Closed += (_, _) => { _keepOnTop.Stop(); _tick.Stop(); };
    }

    /// <summary>Z순서 맨 위로 되돌린다. 위치나 포커스는 그대로 둔다.</summary>
    private void BringToTop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>작업 전환기에서 숨기고, 클릭 메시지를 직접 받도록 준비한다.</summary>
    private void HookWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;

        int style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExToolWindow | WsExNoActivate);

        // WS_EX_NOACTIVATE 창은 클릭해도 활성화되지 않아 WPF의 마우스 이벤트가
        // 오지 않는다. 창 메시지를 직접 받아 클릭을 알아낸다.
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    private const int WmLButtonUp = 0x0202;
    private const int WmNcLButtonUp = 0x00A2;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmLButtonUp or WmNcLButtonUp)
        {
            Clicked?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    // ---- 테마 ----

    private void ApplyTheme()
    {
        bool dark = IsSystemDarkTheme();
        void Set(string key, Color c) => Resources[key] = new SolidColorBrush(c);

        if (dark)
        {
            Set("BarBrush", Color.FromArgb(0xDE, 0x2B, 0x2B, 0x2B));
            Set("BarBorderBrush", Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            Set("BarTextBrush", Color.FromRgb(0xF2, 0xF2, 0xF2));
            Set("BarSubtleBrush", Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
            Set("BarTrackBrush", Color.FromArgb(0x52, 0x60, 0x60, 0x60));
        }
        else
        {
            Set("BarBrush", Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));
            Set("BarBorderBrush", Color.FromArgb(0x24, 0x00, 0x00, 0x00));
            Set("BarTextBrush", Color.FromRgb(0x1F, 0x1F, 0x1F));
            Set("BarSubtleBrush", Color.FromArgb(0xB4, 0x00, 0x00, 0x00));
            Set("BarTrackBrush", Color.FromArgb(0x40, 0x90, 0x90, 0x90));
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
            return true;
        }
    }

    // ---- 내용 ----

    /// <summary>수치를 다시 그린다. 보여줄 것이 없으면 창을 숨긴다.</summary>
    public void Render(IReadOnlyList<ProviderUsage> usages)
    {
        _shown = usages;
        Items.Children.Clear();
        Items.ColumnDefinitions.Clear();

        int column = 0;
        foreach (var u in usages)
        {
            if (!u.IsAvailable || u.Windows.Count == 0) continue;

            Items.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var block = BuildBlock(u, first: column == 0);
            Grid.SetColumn(block, column);
            Items.Children.Add(block);

            column++;
        }

        if (column == 0)
        {
            Hide();
            return;
        }

        // 내용이 바뀌면 폭이 달라지므로 위치를 다시 잡는다.
        UpdateLayout();
        Reposition();
    }

    /// <summary>
    /// 도구 하나를 블록으로 그린다. 위 칸은 세션(5시간), 아래 칸은 주간으로
    /// 자리가 고정돼 있어서, 한쪽이 없어도 다른 쪽 위치가 흔들리지 않는다.
    /// </summary>
    private FrameworkElement BuildBlock(ProviderUsage u, bool first)
    {
        var accent = ResolveColor(u.Provider);

        // 서비스에 문제가 있으면 값 글자로 알린다.
        // 성능 저하는 깜빡이고, 그보다 심하면 빨간색으로 고정한다.
        var status = StatusResolver?.Invoke(u.Provider);
        var health = status?.Health ?? ServiceHealth.Unknown;

        var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var session = u.Windows.FirstOrDefault(w => w.Kind == WindowKind.Session);
        var weekly = u.Windows.FirstOrDefault(w => w.Kind == WindowKind.Weekly);

        // 어느 쪽에도 속하지 않는 한도는 뒤에 덧붙인다.
        var others = u.Windows.Where(w => w.Kind == WindowKind.Other).ToList();

        // 위 칸: 세션. 없으면 빈 자리로 남겨 주간이 아래에 오게 한다.
        if (session is not null)
            rows.Children.Add(BuildRow(session, accent, health));
        else if (weekly is not null)
            rows.Children.Add(BuildPlaceholder());

        if (weekly is not null)
            rows.Children.Add(BuildRow(weekly, accent, health));

        foreach (var w in others)
            rows.Children.Add(BuildRow(w, accent, health));

        // 바깥 배경이나 테두리는 두지 않는다. 진행률 바가 이미 경계를 만든다.
        return new Border
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(first ? 0 : 8, 0, 0, 0),
            Child = rows,
            ToolTip = BuildTooltip(u),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    /// <summary>
    /// 한도 한 줄. 회색 바탕 위에 쓴 만큼 색을 칠하고, 그 위에 값과 남은 시간을 얹는다.
    /// </summary>
    /// <param name="health">서비스 상태. 값 글자의 색과 깜빡임을 정한다.</param>
    private UIElement BuildRow(UsageWindow w, Color accent, ServiceHealth health)
    {
        double used = Math.Clamp(w.Percent, 0, 100);
        double shown = DisplayMode == DisplayMode.Remaining ? 100 - used : used;

        // 바와 숫자는 같은 것을 가리켜야 한다. 남은 양으로 보고 있으면
        // 바도 남은 만큼 차고, 줄어드는 것이 눈에 보인다.
        var fill = new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = RowWidth * shown / 100.0,
        };

        var text = new Grid { Margin = new Thickness(6, 0, 6, 0) };
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 글자가 색 채움 위에 걸치기도 하고 회색 트랙 위에 오기도 한다.
        // 어느 쪽에서도 읽히도록 옅은 외곽 그림자를 준다.
        var value = new TextBlock
        {
            Text = $"{shown:F0}%",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            // 문제가 있을 때만 빨강. 남은 시간은 평소 색을 유지한다.
            Foreground = health is ServiceHealth.Degraded
                or ServiceHealth.PartialOutage
                or ServiceHealth.MajorOutage
                ? new SolidColorBrush(Alert)
                : (Brush)Resources["BarTextBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Effect = TextGlow(),
        };

        // 성능 저하는 아직 쓸 수는 있는 상태다. 빨강으로 고정하기보다
        // 깜빡여서 "지금 불안정하다"는 것을 알린다.
        if (health == ServiceHealth.Degraded)
            StartBlink(value);
        Grid.SetColumn(value, 0);
        text.Children.Add(value);

        var time = new TextBlock
        {
            Text = w.ResetShort,
            FontSize = 10,
            Foreground = (Brush)Resources["BarSubtleBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(time, 1);
        text.Children.Add(time);

        // 회색 트랙 → 색 채움 → 글자 순으로 겹친다.
        var track = new Grid { Width = RowWidth, Height = RowHeight, Margin = new Thickness(0, 1, 0, 1) };
        track.Children.Add(new Border
        {
            Background = (Brush)Resources["BarTrackBrush"],
            CornerRadius = new CornerRadius(3),
        });
        track.Children.Add(fill);
        track.Children.Add(text);

        return track;
    }

    /// <summary>경고에 쓰는 빨강.</summary>
    private static readonly Color Alert = Color.FromRgb(0xE8, 0x30, 0x3C);

    /// <summary>
    /// 빨강과 기본 글자색을 오가며 깜빡이게 한다.
    /// 애니메이션은 글자 하나에만 걸리므로 다시 그려도 새로 시작된다.
    /// </summary>
    private void StartBlink(TextBlock target)
    {
        var normal = ((SolidColorBrush)Resources["BarTextBrush"]).Color;

        // Foreground를 통째로 바꾸면 브러시가 공유되어 다른 글자까지 물든다.
        // 이 글자만의 브러시를 새로 만들어 그 Color만 흔든다.
        var brush = new SolidColorBrush(Alert);
        target.Foreground = brush;

        var blink = new System.Windows.Media.Animation.ColorAnimation
        {
            From = Alert,
            To = normal,
            Duration = TimeSpan.FromMilliseconds(700),
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };

        brush.BeginAnimation(SolidColorBrush.ColorProperty, blink);
    }

    /// <summary>
    /// 배경색이 무엇이든 글자가 읽히게 하는 옅은 발광 효과.
    /// 밝은 테마에서는 흰 빛, 어두운 테마에서는 검은 빛을 깔아 대비를 만든다.
    /// </summary>
    private System.Windows.Media.Effects.Effect TextGlow() =>
        new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = IsSystemDarkTheme() ? Colors.Black : Colors.White,
            ShadowDepth = 0,
            BlurRadius = 4,
            Opacity = 0.85,
        };

    /// <summary>비어 있는 칸. 다른 도구와 줄 위치를 맞추기 위해 자리만 차지한다.</summary>
    private UIElement BuildPlaceholder() => new Grid
    {
        Width = RowWidth,
        Height = RowHeight,
        Margin = new Thickness(0, 1, 0, 1),
    };

    /// <summary>블록에 마우스를 올리면 어느 도구의 무슨 한도인지 알려준다.</summary>
    private string BuildTooltip(ProviderUsage u)
    {
        var lines = new List<string> { u.Provider + (string.IsNullOrEmpty(u.PlanName) ? "" : $" ({u.PlanName})") };

        var status = StatusResolver?.Invoke(u.Provider);
        if (status is not null && status.Health != ServiceHealth.Unknown)
            lines.Add($"서비스 상태: {status.Label}");

        foreach (var w in u.Windows)
        {
            double used = Math.Clamp(w.Percent, 0, 100);
            double shown = DisplayMode == DisplayMode.Remaining ? 100 - used : used;
            string suffix = DisplayMode == DisplayMode.Remaining ? "남음" : "사용";
            lines.Add($"{w.Label}: {shown:F0}% {suffix}" +
                      (string.IsNullOrEmpty(w.ResetText) ? "" : $" · {w.ResetText}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private Color ResolveColor(string provider)
    {
        string hex = ColorResolver?.Invoke(provider) ?? "#8B8B8B";
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

    // ---- 위치 ----

    /// <summary>
    /// 작업표시줄 위, 알림 영역 왼쪽에 붙인다.
    /// 작업표시줄이 위/아래/좌/우 어디에 있든 그 안쪽에 자리잡는다.
    /// </summary>
    public void Reposition()
    {
        if (ActualWidth <= 0) UpdateLayout();

        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) return;

        var work = screen.WorkingArea;
        var full = screen.Bounds;

        double scale = GetDpiScale();
        double pw = ActualWidth * scale;
        double ph = ActualHeight * scale;

        // 작업표시줄과 맞닿는 쪽에 두는 여백.
        const int Gap = 2;

        double x, y;

        if (work.Bottom < full.Bottom)          // 작업표시줄: 아래
        {
            int barTop = work.Bottom;
            int barHeight = full.Bottom - work.Bottom;

            x = work.Right - pw - TrayWidthGuess(scale);

            // 작업표시줄 안에 세로 가운데로 놓는다.
            // 막대가 작업표시줄보다 높으면 아래를 살짝 띄우고 위로 넘치게 둔다.
            y = ph <= barHeight
                ? barTop + (barHeight - ph) / 2
                : full.Bottom - ph - Gap * scale;
        }
        else if (work.Top > full.Top)           // 위
        {
            int barHeight = work.Top - full.Top;

            x = work.Right - pw - TrayWidthGuess(scale);
            y = ph <= barHeight
                ? full.Top + (barHeight - ph) / 2
                : full.Top + Gap * scale;
        }
        else if (work.Left > full.Left)         // 왼쪽
        {
            x = full.Left + (work.Left - full.Left - pw) / 2;
            y = work.Bottom - ph - 8 * scale;
        }
        else if (work.Right < full.Right)       // 오른쪽
        {
            x = work.Right + (full.Right - work.Right - pw) / 2;
            y = work.Bottom - ph - 8 * scale;
        }
        else                                    // 자동 숨김
        {
            x = work.Right - pw - 12 * scale;
            y = work.Bottom - ph - 4 * scale;
        }

        // 화면 밖으로 나가지 않게 가둔다.
        x = Math.Clamp(x, full.Left, Math.Max(full.Left, full.Right - pw));
        y = Math.Clamp(y, full.Top, Math.Max(full.Top, full.Bottom - ph));

        Left = x / scale;
        Top = y / scale;
    }

    /// <summary>
    /// 알림 영역 폭을 재서 그만큼 왼쪽으로 비켜준다.
    /// 못 재면 넉넉한 기본값을 쓴다.
    /// </summary>
    private static double TrayWidthGuess(double scale)
    {
        try
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (notify != IntPtr.Zero && GetWindowRect(notify, out var r))
                    return (r.Right - r.Left) + 8 * scale;
            }
        }
        catch
        {
            // 셸 구조가 다를 수 있다. 아래 기본값으로 넘어간다.
        }

        return 300 * scale;
    }

    private double GetDpiScale()
    {
        var src = PresentationSource.FromVisual(this);
        return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }
}
