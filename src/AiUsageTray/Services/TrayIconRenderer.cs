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
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using (var brush = new SolidBrush(background))
            using (var path = RoundedRect(new RectangleF(0, 0, big, big), big * 0.16f))
            {
                g.FillPath(brush, path);
            }

            string text = ((int)Math.Round(Math.Clamp(value, 0, 100))).ToString();

            // 세 자리(100)는 글자를 줄여야 들어간다.
            float fontSize = text.Length >= 3 ? big * 0.46f : big * 0.62f;

            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            // 글꼴의 시각적 중심이 살짝 아래라 조금 올려 그린다.
            g.DrawString(text, font, white,
                new RectangleF(0, -big * 0.02f, big, big), format);
        }

        using var small = new Bitmap(bmp, new Size(size, size));
        return FromBitmap(small);
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
