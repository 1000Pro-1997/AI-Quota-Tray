using System;
using System.Collections.Generic;
using AiUsageTray.Services;

namespace AiUsageTray.Models;

/// <summary>
/// 한도의 종류. 화면에서 자리를 고정하는 데 쓴다.
/// 라벨 문자열로 분류하면 표기가 바뀔 때 깨지므로 값으로 들고 다닌다.
/// </summary>
public enum WindowKind
{
    /// <summary>몇 시간짜리 세션 한도. 위 칸.</summary>
    Session,

    /// <summary>주 단위 한도. 아래 칸.</summary>
    Weekly,

    /// <summary>둘 중 어느 쪽도 아닌 것.</summary>
    Other,
}

/// <summary>한도 창(window) 하나의 사용 현황.</summary>
public sealed class UsageWindow
{
    /// <summary>어느 칸에 놓을지 정하는 분류.</summary>
    public WindowKind Kind { get; init; } = WindowKind.Other;

    /// <summary>
    /// 분류에 맞지 않는 창의 이름. Kind가 Other일 때만 쓴다.
    /// 번역할 수 없는 값이라 서버가 준 말을 그대로 둔다.
    /// </summary>
    public string RawLabel { get; init; } = "";

    /// <summary>
    /// 화면에 보여줄 이름. 저장해두지 않고 그때그때 현재 언어로 만든다.
    /// 캐시에 번역된 문자열을 넣으면 언어를 바꿔도 옛 말이 남는다.
    /// </summary>
    public string Label => Kind switch
    {
        WindowKind.Session => Strings.Get("window.session"),
        WindowKind.Weekly => Strings.Get("window.weekly"),
        _ => string.IsNullOrEmpty(RawLabel) ? Strings.Get("window.usage") : RawLabel,
    };

    /// <summary>0~100 사용률.</summary>
    public double Percent { get; init; }

    /// <summary>한도가 초기화되는 시각(로컬). 알 수 없으면 null.</summary>
    public DateTime? ResetsAt { get; init; }

    /// <summary>
    /// 리셋까지 남은 시간을 아주 짧게. 위젯바처럼 폭이 좁은 곳에 쓴다.
    /// 예: "2일 3시간" -> "2d 3h", "1시간 32분" -> "1h 32m"
    /// </summary>
    public string ResetShort
    {
        get
        {
            if (ResetsAt is null) return "";

            var span = ResetsAt.Value - DateTime.Now;
            if (span <= TimeSpan.Zero) return Strings.Get("short.soon");

            if (span.TotalDays >= 1)
                return Strings.Get("age.days", (int)span.TotalDays) + " " +
                       Strings.Get("age.hours", span.Hours);

            if (span.TotalHours >= 1)
                return Strings.Get("age.hours", (int)span.TotalHours) + " " +
                       Strings.Get("age.minutes", span.Minutes);

            return Strings.Get("age.minutes", span.Minutes);
        }
    }

    /// <summary>리셋까지 남은 시간을 "3시간 20분" 형태로.</summary>
    public string ResetText
    {
        get
        {
            if (ResetsAt is null) return "";
            var span = ResetsAt.Value - DateTime.Now;
            if (span <= TimeSpan.Zero) return Strings.Get("reset.soon");
            if (span.TotalDays >= 1) return Strings.Get("reset.days", (int)span.TotalDays, span.Hours);
            if (span.TotalHours >= 1) return Strings.Get("reset.hours", (int)span.TotalHours, span.Minutes);
            return Strings.Get("reset.minutes", span.Minutes);
        }
    }
}

/// <summary>공급자 하나(Claude, Codex 등)의 전체 사용 현황.</summary>
public sealed class ProviderUsage
{
    public string Provider { get; init; } = "";

    /// <summary>Pro, Plus 등 요금제 이름. 모르면 빈 문자열.</summary>
    public string PlanName { get; init; } = "";

    public IReadOnlyList<UsageWindow> Windows { get; init; } = Array.Empty<UsageWindow>();

    /// <summary>토큰 누적치. 알 수 없으면 null.</summary>
    public TokenTotals? Tokens { get; init; }

    /// <summary>이 수치를 마지막으로 확인한 시각(로컬).</summary>
    public DateTime? LastUpdated { get; init; }

    /// <summary>조회에 실패했을 때의 사유. 성공이면 null.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// 마지막 조회는 실패했지만 이전 성공값을 대신 보여주는 중이라는 표시.
    /// 이때 Error에는 왜 갱신하지 못했는지가 담긴다.
    /// </summary>
    public bool IsStale { get; init; }

    /// <summary>보여줄 수치가 있는가. 값이 낡았어도 없는 것보다는 낫다.</summary>
    public bool IsAvailable => Error is null || IsStale;

    /// <summary>
    /// 갱신에 실패했을 때, 이 값을 이전 성공값의 사본으로 되살린다.
    /// 화면에는 직전 수치가 계속 보이고 왜 멈췄는지만 덧붙는다.
    /// </summary>
    public ProviderUsage AsStale(string reason) => new()
    {
        Provider = Provider,
        PlanName = PlanName,
        Windows = Windows,
        Tokens = Tokens,
        LastUpdated = LastUpdated,
        Error = reason,
        IsStale = true,
    };

    /// <summary>가장 압박이 심한 창의 사용률. 트레이 아이콘 색상 결정에 사용.</summary>
    public double PeakPercent
    {
        get
        {
            double peak = 0;
            foreach (var w in Windows)
                if (w.Percent > peak) peak = w.Percent;
            return peak;
        }
    }

    public static ProviderUsage Unavailable(string provider, string reason) =>
        new() { Provider = provider, Error = reason };
}

/// <summary>기간 내 토큰 합계.</summary>
public sealed class TokenTotals
{
    public long Input { get; init; }
    public long Output { get; init; }
    public long CacheRead { get; init; }
    public long CacheWrite { get; init; }

    /// <summary>캐시 읽기를 제외한 실질 소비량.</summary>
    public long Billable => Input + Output + CacheWrite;

    public long Total => Input + Output + CacheRead + CacheWrite;
}
