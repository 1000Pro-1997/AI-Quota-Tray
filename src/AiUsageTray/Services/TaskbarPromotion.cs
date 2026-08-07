using System;
using System.Linq;
using Microsoft.Win32;

namespace AiUsageTray.Services;

/// <summary>
/// 트레이 아이콘을 숨김 영역(^)에서 꺼내 작업표시줄에 항상 보이게 한다.
///
/// Windows 11은 알림 영역 아이콘마다 레지스트리 항목을 만들고, 그 안의
/// IsPromoted가 1이면 작업표시줄에 직접 표시한다. 항목의 키 이름은 실행 파일
/// 경로에서 만들어지는 해시라서 미리 알 수 없다. 대신 각 항목의
/// ExecutablePath 값을 비교해 자기 항목을 찾는다.
///
/// 항목은 트레이 아이콘이 최소 한 번 표시된 뒤에야 생기므로, 아이콘을 띄우기
/// 전에 호출하면 찾지 못한다.
/// </summary>
public static class TaskbarPromotion
{
    private const string NotifyIconRoot = @"Control Panel\NotifyIconSettings";
    private const string PromotedValue = "IsPromoted";
    private const string PathValue = "ExecutablePath";

    /// <summary>이 기능을 쓸 수 있는 Windows인지. Win11에서만 의미가 있다.</summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(NotifyIconRoot);
                return key is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 현재 상태를 돌려준다. 아직 항목이 없으면(아이콘을 띄운 적이 없으면) null.
    /// </summary>
    public static bool? IsPromoted()
    {
        try
        {
            using var entry = OpenOwnEntry(writable: false);
            if (entry is null) return null;

            return entry.GetValue(PromotedValue) is int v && v == 1;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 작업표시줄 고정을 켜거나 끈다.
    /// </summary>
    /// <returns>실패했으면 사유, 성공이면 null.</returns>
    public static string? SetPromoted(bool promoted)
    {
        if (!IsSupported)
            return "이 Windows 버전에서는 지원되지 않습니다.";

        try
        {
            using var entry = OpenOwnEntry(writable: true);

            if (entry is null)
            {
                return "트레이 아이콘 항목을 아직 찾을 수 없습니다. " +
                       "잠시 후 다시 시도하세요.";
            }

            entry.SetValue(PromotedValue, promoted ? 1 : 0, RegistryValueKind.DWord);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "레지스트리에 쓸 권한이 없습니다.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 실행 파일 경로가 일치하는 항목을 찾는다.
    /// 같은 exe라도 경로가 다르면 별도 항목이 생기므로 전체 경로로 비교한다.
    /// </summary>
    private static RegistryKey? OpenOwnEntry(bool writable)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return null;

        using var root = Registry.CurrentUser.OpenSubKey(NotifyIconRoot);
        if (root is null) return null;

        foreach (string name in root.GetSubKeyNames())
        {
            RegistryKey? sub = null;
            try
            {
                sub = root.OpenSubKey(name, writable);
                if (sub is null) continue;

                if (sub.GetValue(PathValue) is string path &&
                    string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    return sub; // 호출자가 Dispose한다.
                }

                sub.Dispose();
            }
            catch
            {
                sub?.Dispose();
                // 읽을 수 없는 항목은 건너뛴다.
            }
        }

        return null;
    }
}
