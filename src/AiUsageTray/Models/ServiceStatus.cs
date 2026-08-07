using System;
using AiUsageTray.Services;

namespace AiUsageTray.Models;

/// <summary>
/// 서비스 장애 수준. Statuspage의 컴포넌트 상태를 옮긴 것이다.
/// 값이 클수록 심각하다.
/// </summary>
public enum ServiceHealth
{
    /// <summary>상태를 확인하지 못했다.</summary>
    Unknown = 0,

    /// <summary>정상.</summary>
    Operational = 1,

    /// <summary>성능 저하. 동작은 하지만 느리거나 불안정하다.</summary>
    Degraded = 2,

    /// <summary>일부 장애.</summary>
    PartialOutage = 3,

    /// <summary>전면 장애.</summary>
    MajorOutage = 4,

    /// <summary>점검 중.</summary>
    Maintenance = 5,
}

/// <summary>한 서비스의 장애 상태.</summary>
public sealed class ServiceStatus
{
    public ServiceHealth Health { get; init; } = ServiceHealth.Unknown;

    /// <summary>확인한 시각(로컬).</summary>
    public DateTime? CheckedAt { get; init; }

    /// <summary>사용자에게 보여줄 짧은 말.</summary>
    public string Label => Strings.Get(Health switch
    {
        ServiceHealth.Operational => "status.operational",
        ServiceHealth.Degraded => "status.degraded",
        ServiceHealth.PartialOutage => "status.partialOutage",
        ServiceHealth.MajorOutage => "status.majorOutage",
        ServiceHealth.Maintenance => "status.maintenance",
        _ => "status.unknown",
    });

    /// <summary>상태 점 색. #RRGGBB.</summary>
    public string Color => Health switch
    {
        ServiceHealth.Operational => "#3FB950",   // 초록
        ServiceHealth.Degraded => "#F7A32D",      // 주황
        ServiceHealth.PartialOutage => "#F0883E", // 진한 주황
        ServiceHealth.MajorOutage => "#E84855",   // 빨강
        ServiceHealth.Maintenance => "#58A6FF",   // 파랑
        _ => "#8B8B8B",                           // 회색
    };

    /// <summary>정상이 아니면 눈에 띄게 알릴 가치가 있다.</summary>
    public bool NeedsAttention =>
        Health is ServiceHealth.Degraded
            or ServiceHealth.PartialOutage
            or ServiceHealth.MajorOutage;

    public static ServiceStatus Unknown() => new();
}
