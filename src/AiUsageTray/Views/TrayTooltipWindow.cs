using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace AiUsageTray.Views;

/// <summary>툴팁의 한 줄. 제목 줄은 굵게, 부가 정보는 흐리게 그린다.</summary>
public readonly record struct TooltipLine(string Text, TooltipLineKind Kind = TooltipLineKind.Normal);

public enum TooltipLineKind { Header, Normal, Subtle }

/// <summary>
/// 트레이 아이콘에 마우스를 올렸을 때 뜨는 여러 줄 툴팁.
/// Windows 트레이 툴팁은 127자에서 잘려 5시간·주간·토큰을 다 담을 수 없어 직접 그린다.
/// 포커스를 가져가면 작업 중이던 창이 비활성화되므로 절대 활성화하지 않는다.
/// </summary>
public sealed class TrayTooltipWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private readonly StackPanel _lines = new();
    private readonly Border _frame;
    private string _shownText = "";

    public TrayTooltipWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        _frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 8),
            Margin = new Thickness(8),
            Child = _lines,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.25, Color = Colors.Black,
            },
        };
        Content = _frame;

        SourceInitialized += (_, _) =>
        {
            // 마우스가 툴팁 위로 지나가도 아이콘 위에 있는 것처럼 굴어야 하므로 클릭도 통과시킨다.
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE,
                GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        };
    }

    /// <summary>내용을 바꾼다. 같은 글이면 다시 그리지 않는다. 매 틱 불려도 깜빡이지 않게.</summary>
    public void SetLines(IReadOnlyList<TooltipLine> lines)
    {
        string text = string.Join("\n", lines);
        if (text == _shownText) return;
        _shownText = text;

        bool dark = IsSystemDarkTheme();
        var textBrush = new SolidColorBrush(dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1A, 0x1A, 0x1A));
        var subtleBrush = new SolidColorBrush(dark ? Color.FromRgb(0x94, 0x94, 0x94) : Color.FromRgb(0x70, 0x70, 0x74));
        _frame.Background = new SolidColorBrush(dark ? Color.FromRgb(0x27, 0x27, 0x27) : Colors.White);
        _frame.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0, 0, 0));

        _lines.Children.Clear();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            _lines.Children.Add(new TextBlock
            {
                Text = line.Text,
                FontSize = line.Kind == TooltipLineKind.Header ? 12 : 11.5,
                FontWeight = line.Kind == TooltipLineKind.Header ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = line.Kind == TooltipLineKind.Subtle ? subtleBrush : textBrush,
                // 공급자 사이를 띄워 덩어리가 보이게 한다.
                Margin = new Thickness(0, line.Kind == TooltipLineKind.Header && i > 0 ? 7 : 1, 0, 0),
            });
        }
    }

    /// <summary>커서 위(작업표시줄 반대편)에 띄운다. 활성화하지 않는다.</summary>
    public void ShowNearCursor()
    {
        Opacity = 0;
        Show();
        UpdateLayout();

        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var work = screen.WorkingArea;
        var full = screen.Bounds;

        double scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double pw = ActualWidth * scale;
        double ph = ActualHeight * scale;

        // 작업표시줄이 어느 변에 있든 그 안쪽으로 띄운다. 팝업과 같은 규칙이다.
        double x = cursor.X - pw / 2, y = cursor.Y - ph - 8 * scale;
        if (work.Top > full.Top) y = work.Top;
        else if (work.Left > full.Left) { x = work.Left; y = cursor.Y - ph / 2; }
        else if (work.Right < full.Right) { x = work.Right - pw; y = cursor.Y - ph / 2; }
        else if (work.Bottom < full.Bottom) y = work.Bottom - ph;

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - pw));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - ph));

        Left = x / scale;
        Top = y / scale;
        Opacity = 1;
    }

    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return true;
        }
    }
}
