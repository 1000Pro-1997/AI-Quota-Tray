using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiUsageTray.Services;

namespace AiUsageTray.Views;

/// <summary>
/// 색을 고르는 작은 팝업. 자주 쓰는 12색을 먼저 보여주고,
/// 원하는 색이 없으면 Windows 색 선택 대화상자로 넘어간다.
/// </summary>
public partial class ColorPickerWindow : Window
{
    private const string ThemeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// 기본 팔레트. 진행률 바에 쓰이므로 흰 글씨가 얹혀도 읽히도록
    /// 너무 밝지 않은 색으로 골랐다.
    /// </summary>
    private static readonly string[] Palette12 =
    {
        "#F7A32D", // 주황 (Claude 기본)
        "#E8833A", // 호박
        "#E84855", // 빨강
        "#E45A92", // 분홍
        "#58A6FF", // 파랑 (Codex 기본)
        "#3D7DD8", // 진한 파랑
        "#6C7BE8", // 남보라
        "#9B6BE8", // 보라
        "#3FB950", // 초록
        "#2FA98A", // 청록
        "#B8952F", // 황토
        "#7A8290", // 회색
    };

    /// <summary>사용자가 고른 색. 취소하면 null.</summary>
    public string? Selected { get; private set; }

    private readonly string _defaultColor;

    /// <param name="title">어느 도구의 색인지 알려주는 제목.</param>
    /// <param name="current">지금 색. 팔레트에 있으면 표시된다.</param>
    /// <param name="defaultColor">초기화를 눌렀을 때 돌아갈 색.</param>
    public ColorPickerWindow(string title, string current, string defaultColor)
    {
        _defaultColor = defaultColor;

        InitializeComponent();
        ApplyTheme();

        TitleText.Text = title;
        CustomButton.Content = Strings.Get("color.custom");
        ResetButton.Content = Strings.Get("color.reset");

        BuildPalette(current);

        // 바깥을 클릭하거나 Esc를 누르면 고르지 않고 닫는다.
        Deactivated += (_, _) => CloseWith(null);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) CloseWith(null); };
    }

    private void BuildPalette(string current)
    {
        foreach (string hex in Palette12)
        {
            var chip = new RadioButton
            {
                Style = (Style)Resources["Chip"],
                GroupName = "Palette",
                Tag = new SolidColorBrush(Parse(hex)),
                IsChecked = SameColor(hex, current),
                ToolTip = hex,
            };

            // 누르는 즉시 고르고 닫는다. 확인 버튼을 한 번 더 누를 이유가 없다.
            chip.Checked += (_, _) => CloseWith(hex);

            Palette.Children.Add(chip);
        }
    }

    /// <summary>Windows 기본 색 선택 대화상자로 넘어간다.</summary>
    private void OnCustom(object sender, RoutedEventArgs e)
    {
        // 이 창이 먼저 닫히면 Deactivated가 취소로 처리해버린다.
        _closing = true;

        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = ToDrawing(Selected ?? _defaultColor),
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dlg.Color;
            Selected = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        Close();
    }

    private void OnReset(object sender, RoutedEventArgs e) => CloseWith(_defaultColor);

    /// <summary>고른 색을 담고 닫는다. null이면 취소.</summary>
    private void CloseWith(string? hex)
    {
        if (_closing) return;
        _closing = true;

        Selected = hex;
        Close();
    }

    /// <summary>닫는 중에 Deactivated가 겹쳐 들어오는 것을 막는다.</summary>
    private bool _closing;

    /// <summary>버튼 아래에 붙여 띄운다. 화면 밖으로 나가지 않게 가둔다.</summary>
    public void ShowUnder(FrameworkElement anchor, Window owner)
    {
        Owner = owner;

        // 크기를 확정해야 위치를 계산할 수 있다.
        Opacity = 0;
        Show();
        UpdateLayout();

        var topLeft = anchor.PointToScreen(new Point(0, anchor.ActualHeight));
        double scale = GetDpiScale();

        double x = topLeft.X - 10 * scale;   // 카드 여백만큼 보정
        double y = topLeft.Y - 2 * scale;

        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)topLeft.X, (int)topLeft.Y));
        var work = screen.WorkingArea;

        double w = ActualWidth * scale;
        double h = ActualHeight * scale;

        // 아래로 넘치면 버튼 위쪽으로 띄운다.
        if (y + h > work.Bottom)
            y = anchor.PointToScreen(new Point(0, 0)).Y - h + 10 * scale;

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));

        Left = x / scale;
        Top = y / scale;
        Opacity = 1;

        Activate();
    }

    private double GetDpiScale()
    {
        var src = PresentationSource.FromVisual(this);
        return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    // ---- 테마 ----

    private void ApplyTheme()
    {
        bool dark = IsSystemDarkTheme();
        void Set(string key, Color c) => Resources[key] = new SolidColorBrush(c);

        if (dark)
        {
            Set("CardBrush", Color.FromRgb(0x27, 0x27, 0x27));
            Set("BorderBrush2", Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
            Set("TextBrush", Color.FromRgb(0xF2, 0xF2, 0xF2));
            Set("SubtleBrush", Color.FromRgb(0x94, 0x94, 0x94));
            Set("HoverBrush", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            Set("AccentBrush", Color.FromRgb(0x4C, 0x8E, 0xF0));
        }
        else
        {
            Set("CardBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            Set("BorderBrush2", Color.FromArgb(0x22, 0x00, 0x00, 0x00));
            Set("TextBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            Set("SubtleBrush", Color.FromRgb(0x70, 0x70, 0x74));
            Set("HoverBrush", Color.FromArgb(0x12, 0x00, 0x00, 0x00));
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

    // ---- 색 변환 ----

    private static Color Parse(string hex)
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

    private static System.Drawing.Color ToDrawing(string hex)
    {
        var c = Parse(hex);
        return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
    }

    /// <summary>표기가 달라도 같은 색이면 같다고 본다(#abc vs #AABBCC).</summary>
    private static bool SameColor(string a, string b)
    {
        var ca = Parse(a);
        var cb = Parse(b);
        return ca.R == cb.R && ca.G == cb.G && ca.B == cb.B;
    }
}
