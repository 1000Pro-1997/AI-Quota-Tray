using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AiUsageTray.Services;

/// <summary>
/// 최고 사용률을 도넛 게이지로 그려 트레이 아이콘을 만든다.
/// GDI 아이콘 핸들은 반드시 DestroyIcon으로 해제해야 하므로 호출자가 Dispose를 책임진다.
/// </summary>
public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 색상은 언제나 <paramref name="usedPercent"/>(쓴 비율) 기준이다.
    /// 화면에 남은 양을 보여주더라도 위험 판단 기준은 바뀌지 않는다.
    /// </summary>
    public static Color ColorFor(double usedPercent) => usedPercent switch
    {
        >= 90 => Color.FromArgb(232, 72, 85),    // 위험 (빨강)
        >= 75 => Color.FromArgb(247, 163, 45),   // 경고 (주황)
        _ => Color.FromArgb(88, 166, 255),       // 정상 (파랑)
    };

    /// <summary>
    /// 도넛 게이지 아이콘 생성. 데이터가 없으면 회색 빈 링을 그린다.
    /// </summary>
    /// <param name="percent">0~100. null이면 데이터 없음 상태.</param>
    /// <param name="size">아이콘 한 변. DPI에 따라 16/20/24/32를 넘긴다.</param>
    public static Icon Render(double? percent, int size = 32)
    {
        // 안티앨리어싱 품질을 위해 4배로 그린 뒤 축소한다.
        const int Scale = 4;
        int big = size * Scale;

        using var bmp = new Bitmap(big, big);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float thickness = big * 0.18f;
            float inset = thickness / 2f + big * 0.06f;
            var rect = new RectangleF(inset, inset, big - inset * 2, big - inset * 2);

            // 바닥 링. 남은 용량을 나타낸다.
            using (var track = new Pen(Color.FromArgb(70, 255, 255, 255), thickness))
            {
                track.StartCap = track.EndCap = LineCap.Round;
                g.DrawEllipse(track, rect);
            }

            if (percent is { } p)
            {
                double clamped = Math.Clamp(p, 0, 100);
                float sweep = (float)(clamped / 100.0 * 360.0);

                // 0%여도 존재감이 있도록 최소 각도를 준다.
                if (sweep > 0 && sweep < 8) sweep = 8;

                // 100% 미만인데 링이 닫혀 보이면 한도 소진과 구분이 안 된다.
                // 둥근 선 끝이 양쪽에서 틈을 메우므로 넉넉히 45도를 남긴다.
                if (clamped < 100 && sweep > 315) sweep = 315;

                if (clamped >= 100)
                {
                    // 한도 소진: 꽉 찬 원. 링과 즉시 구분된다.
                    using var full = new SolidBrush(ColorFor(clamped));
                    float r = big * 0.34f;
                    g.FillEllipse(full, big / 2f - r, big / 2f - r, r * 2, r * 2);
                }
                else if (sweep > 0)
                {
                    using var arc = new Pen(ColorFor(clamped), thickness);
                    arc.StartCap = arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, rect, -90, sweep); // 12시 방향에서 시계 방향
                }
            }
            else
            {
                // 데이터 없음: 가운데 점 하나.
                float dot = big * 0.16f;
                using var brush = new SolidBrush(Color.FromArgb(140, 255, 255, 255));
                g.FillEllipse(brush, big / 2f - dot / 2f, big / 2f - dot / 2f, dot, dot);
            }
        }

        using var small = new Bitmap(bmp, new Size(size, size));
        return FromBitmap(small);
    }

    /// <summary>
    /// 도구 색 배경에 숫자를 얹은 아이콘. 값이 한눈에 읽혀 도넛보다 정보가 많다.
    /// 16px에서도 두 자리가 선명하도록 배경을 꽉 채우고 흰 글씨를 쓴다.
    /// </summary>
    /// <param name="value">0~100. 표기 모드에 따라 남은 양이거나 쓴 양이다.</param>
    /// <param name="background">도구를 구분하는 색.</param>
    public static Icon RenderNumber(double value, Color background, int size = 16)
    {
        const int Scale = 8;
        int big = size * Scale;

        using var bmp = new Bitmap(big, big);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            // 모서리를 살짝만 깎은 네모. 둥글수록 작은 크기에서 배경이 야위어
            // 숫자가 앉을 자리가 줄어든다. 네모에 가까울수록 글자가 커진다.
            using (var brush = new SolidBrush(background))
            using (var path = RoundedRect(new RectangleF(0, 0, big, big), big * 0.11f))
            {
                g.FillPath(brush, path);
            }

            string text = ((int)Math.Round(Math.Clamp(value, 0, 100))).ToString();

            // 글자를 상자에 맞춰 키운다. 자릿수마다 고정 크기를 주면 한 자리는
            // 허전하고 세 자리는 넘친다. 실제로 재서 맞추는 편이 늘 꽉 찬다.
            //
            // 사방에 여백을 남긴다. 글자가 모서리에 닿으면 작업표시줄에서
            // 배경색이 사라져 뱃지가 아니라 얼룩처럼 보인다.
            float padX = big * 0.10f;
            float padY = big * 0.13f;
            float fontSize = FitFontSize(g, text, big - padX * 2, big - padY * 2);

            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.White);
            using var format = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None,
            };

            // 글꼴의 시각적 중심이 살짝 아래라 조금 올려 그린다.
            g.DrawString(text, font, white,
                new RectangleF(0, -big * 0.04f, big, big), format);
        }

        using var small = new Bitmap(bmp, new Size(size, size));
        return FromBitmap(small);
    }

    /// <summary>
    /// 값이 아직 없을 때의 뱃지. 숫자 뱃지와 같은 네모라 아이콘이 갑자기
    /// 다른 모양으로 바뀌지 않는다. 색을 죽이고 가운데 줄만 그어 비어 있음을 알린다.
    /// </summary>
    public static Icon RenderEmpty(int size = 16)
    {
        const int Scale = 8;
        int big = size * Scale;

        using var bmp = new Bitmap(big, big);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var brush = new SolidBrush(Color.FromArgb(120, 128, 128, 128)))
            using (var path = RoundedRect(new RectangleF(0, 0, big, big), big * 0.11f))
            {
                g.FillPath(brush, path);
            }

            // 줄 하나. 16px에서도 뭉개지지 않는 유일한 기호다.
            float w = big * 0.44f, h = big * 0.11f;
            using var dash = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            g.FillRectangle(dash, (big - w) / 2f, (big - h) / 2f, w, h);
        }

        using var small = new Bitmap(bmp, new Size(size, size));
        return FromBitmap(small);
    }

    /// <summary>
    /// 글자가 <paramref name="maxWidth"/>·<paramref name="maxHeight"/> 안에 들어가는
    /// 가장 큰 크기를 찾는다. Segoe UI Bold의 숫자 폭은 자릿수에 정비례하지 않아
    /// 계산으로 맞추기 어렵다. 몇 번 재보는 편이 확실하다.
    /// </summary>
    private static float FitFontSize(Graphics g, string text, float maxWidth, float maxHeight)
    {
        float size = maxHeight;

        // 열 번이면 충분히 수렴한다. 못 맞춰도 마지막 값은 원래 크기보다 작다.
        for (int i = 0; i < 10; i++)
        {
            using var probe = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            var m = g.MeasureString(text, probe, PointF.Empty, StringFormat.GenericTypographic);

            if (m.Width <= maxWidth && m.Height <= maxHeight) break;

            float shrink = Math.Min(maxWidth / m.Width, maxHeight / m.Height);
            size *= Math.Min(shrink, 0.97f); // 진동을 막으려 한 번에 다 줄이지 않는다.
        }

        return size;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Icon.FromHandle은 핸들을 소유하지 않으므로 복제 후 원본을 해제한다.</summary>
    private static Icon FromBitmap(Bitmap bmp)
    {
        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
