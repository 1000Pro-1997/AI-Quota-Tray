using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

/// <summary>
/// 각 서비스의 공개 상태 페이지에서 장애 상태를 읽는다.
/// 둘 다 Statuspage를 쓰므로 같은 방식으로 조회한다.
///
/// 사용량과 별개로 동작한다. 상태 조회가 실패해도 사용량 표시에는 영향이 없다.
/// </summary>
public sealed class StatusProvider
{
    /// <summary>
    /// 공급자별 상태 페이지와, 그중 눈여겨볼 컴포넌트 이름.
    /// 전체 상태 대신 이 도구가 실제로 쓰는 부분만 본다.
    /// </summary>
    private static readonly Dictionary<string, (string Url, string[] Components)> Sources = new()
    {
        ["Claude"] = (
            "https://status.claude.com/api/v2/components.json",
            new[] { "Claude Code", "Claude API (api.anthropic.com)" }),

        ["Codex"] = (
            "https://status.openai.com/api/v2/components.json",
            new[] { "Codex in ChatGPT Desktop", "Responses", "API" }),
    };

    /// <summary>
    /// 사람이 열어 보는 상태 페이지. 위의 Url은 JSON API라 브라우저로
    /// 열면 날것의 데이터만 나오므로, 보여줄 주소를 따로 둔다.
    /// </summary>
    private static readonly Dictionary<string, string> Pages = new()
    {
        ["Claude"] = "https://status.claude.com/",
        ["Codex"] = "https://status.openai.com/",
    };

    /// <summary>공급자의 상태 페이지 주소. 모르는 공급자면 null.</summary>
    public static string? PageFor(string provider) =>
        Pages.TryGetValue(provider, out var url) ? url : null;

    /// <summary>상태는 자주 바뀌지 않는다. 이 간격 안에는 캐시를 쓴다.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly Dictionary<string, ServiceStatus> _cache = new();
    private DateTime _lastFetch = DateTime.MinValue;

    public StatusProvider(HttpClient http) => _http = http;

    /// <summary>마지막으로 확인한 상태. 조회 전이면 비어 있다.</summary>
    public IReadOnlyDictionary<string, ServiceStatus> Latest => _cache;

    /// <summary>공급자 이름으로 상태를 찾는다. 모르면 Unknown.</summary>
    public ServiceStatus For(string provider) =>
        _cache.TryGetValue(provider, out var s) ? s : ServiceStatus.Unknown();

    /// <summary>
    /// 화면 확인용 강제 상태. AIUSAGE_FAKE_STATUS 환경변수에
    /// "Claude=degraded_performance,Codex=major_outage" 형태로 넣으면 그대로 쓴다.
    /// 실제 장애를 기다리지 않고 표시를 검증할 때만 쓴다.
    /// </summary>
    private static Dictionary<string, ServiceHealth>? ReadOverride()
    {
        string? raw = Environment.GetEnvironmentVariable("AIUSAGE_FAKE_STATUS");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var map = new Dictionary<string, ServiceHealth>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = part.Split('=', 2);
            if (kv.Length != 2) continue;

            var health = ToHealth(kv[1].Trim());
            if (health != ServiceHealth.Unknown) map[kv[0].Trim()] = health;
        }

        return map.Count > 0 ? map : null;
    }

    /// <summary>상태를 갱신한다. 최근에 확인했으면 건너뛴다.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (ReadOverride() is { } forced)
        {
            foreach (var (provider, health) in forced)
                _cache[provider] = new ServiceStatus { Health = health, CheckedAt = DateTime.Now };

            _lastFetch = DateTime.Now;
            return;
        }

        if (DateTime.Now - _lastFetch < MinInterval) return;

        foreach (var (provider, source) in Sources)
        {
            ct.ThrowIfCancellationRequested();

            var status = await FetchAsync(source.Url, source.Components, ct).ConfigureAwait(false);

            // 확인에 실패했다면 이전 값을 지우지 않는다. 낡아도 없는 것보다 낫다.
            if (status.Health != ServiceHealth.Unknown || !_cache.ContainsKey(provider))
                _cache[provider] = status;
        }

        _lastFetch = DateTime.Now;
    }

    private async Task<ServiceStatus> FetchAsync(string url, string[] wanted, CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return ServiceStatus.Unknown();

            string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(body, wanted);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // 네트워크가 없거나 페이지 구조가 바뀌었다. 상태를 모르는 것으로 둔다.
            return ServiceStatus.Unknown();
        }
    }

    /// <summary>
    /// 관심 컴포넌트 중 가장 나쁜 상태를 그 서비스의 상태로 삼는다.
    /// 이름이 하나도 맞지 않으면 전체 컴포넌트에서 가장 나쁜 것을 본다.
    /// </summary>
    private static ServiceStatus Parse(string json, string[] wanted)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("components", out var components) ||
            components.ValueKind != JsonValueKind.Array)
        {
            return ServiceStatus.Unknown();
        }

        var worstWanted = ServiceHealth.Unknown;
        var worstAny = ServiceHealth.Unknown;

        foreach (var c in components.EnumerateArray())
        {
            // 그룹 머리글은 자식들의 요약이라 중복이다.
            if (c.TryGetProperty("group", out var isGroup) &&
                isGroup.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            string name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string state = c.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";

            var health = ToHealth(state);
            if (health == ServiceHealth.Unknown) continue;

            if (health > worstAny) worstAny = health;

            foreach (string w in wanted)
            {
                if (!string.Equals(name, w, StringComparison.OrdinalIgnoreCase)) continue;
                if (health > worstWanted) worstWanted = health;
                break;
            }
        }

        var result = worstWanted != ServiceHealth.Unknown ? worstWanted : worstAny;

        return new ServiceStatus
        {
            Health = result,
            CheckedAt = result == ServiceHealth.Unknown ? null : DateTime.Now,
        };
    }

    private static ServiceHealth ToHealth(string status) => status switch
    {
        "operational" => ServiceHealth.Operational,
        "degraded_performance" => ServiceHealth.Degraded,
        "partial_outage" => ServiceHealth.PartialOutage,
        "major_outage" => ServiceHealth.MajorOutage,
        "under_maintenance" => ServiceHealth.Maintenance,
        _ => ServiceHealth.Unknown,
    };
}
