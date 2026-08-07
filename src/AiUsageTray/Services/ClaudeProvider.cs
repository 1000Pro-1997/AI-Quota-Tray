using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

/// <summary>
/// Claude Code의 OAuth 자격증명을 읽어 공식 usage 엔드포인트를 조회한다.
/// 자격증명 파일은 절대 수정하지 않는다. 액세스 토큰이 만료되면 Claude Code가
/// 알아서 갱신해 파일에 다시 쓰므로, 여기서는 다시 읽기만 한다.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBeta = "oauth-2025-04-20";

    private readonly HttpClient _http;
    private readonly Func<string> _credentialsPath;

    /// <summary>429를 받으면 이 시각까지는 서버를 다시 부르지 않는다.</summary>
    private DateTime _blockedUntil = DateTime.MinValue;

    public string Name => "Claude";

    public ClaudeProvider(HttpClient http, Func<string> credentialsPath)
    {
        _http = http;
        _credentialsPath = credentialsPath;
    }

    public async Task<ProviderUsage> FetchAsync(CancellationToken ct)
    {
        string path = _credentialsPath();
        if (!File.Exists(path))
            return ProviderUsage.Unavailable(Name, Strings.Get("error.notLoggedIn"));

        string token, plan;
        try
        {
            (token, plan) = ReadCredentials(path);
        }
        catch (Exception ex)
        {
            return ProviderUsage.Unavailable(Name, Strings.Get("error.credentialsUnreadable", ex.Message));
        }

        if (string.IsNullOrEmpty(token))
            return ProviderUsage.Unavailable(Name, Strings.Get("error.noToken"));

        // 아직 백오프 중이면 아예 부르지 않는다. 부르면 429만 더 쌓인다.
        if (DateTime.Now < _blockedUntil)
        {
            int wait = (int)Math.Ceiling((_blockedUntil - DateTime.Now).TotalSeconds);
            return ProviderUsage.Unavailable(Name, Strings.Get("error.rateLimitedWait", wait));
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return ProviderUsage.Unavailable(Name, Strings.Get("error.tokenRefresh"));

            if ((int)res.StatusCode == 429)
            {
                // 서버가 알려주는 대기 시간을 따른다. 없거나 이상하면 넉넉히 2분.
                var fallback = TimeSpan.FromMinutes(2);
                TimeSpan retry;

                if (res.Headers.RetryAfter?.Delta is { } delta)
                    retry = delta;
                else if (res.Headers.RetryAfter?.Date is { } date)
                    retry = date - DateTimeOffset.Now;
                else
                    retry = fallback;

                // 0초나 음수는 즉시 재시도를 뜻하지만, 그러면 429를 또 받는다.
                if (retry < TimeSpan.FromSeconds(30)) retry = fallback;
                if (retry > TimeSpan.FromMinutes(15)) retry = TimeSpan.FromMinutes(15);

                _blockedUntil = DateTime.Now + retry;

                return ProviderUsage.Unavailable(Name,
                    Strings.Get("error.rateLimited", (int)retry.TotalSeconds));
            }

            if (!res.IsSuccessStatusCode)
                return ProviderUsage.Unavailable(Name, Strings.Get("error.httpFailed", (int)res.StatusCode));

            // 성공했으니 백오프를 푼다.
            _blockedUntil = DateTime.MinValue;

            string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(body, plan);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return ProviderUsage.Unavailable(Name, Strings.Get("error.network"));
        }
        catch (Exception ex)
        {
            return ProviderUsage.Unavailable(Name, ex.Message);
        }
    }

    private static (string token, string plan) ReadCredentials(string path)
    {
        // Claude Code가 동시에 쓸 수 있으므로 공유 읽기로 연다.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = JsonDocument.Parse(fs);

        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            return ("", "");

        string token = oauth.TryGetProperty("accessToken", out var t) ? t.GetString() ?? "" : "";
        string plan = oauth.TryGetProperty("subscriptionType", out var s) ? s.GetString() ?? "" : "";
        return (token, Capitalize(plan));
    }

    /// <summary>
    /// 응답의 limits 배열을 우선 사용한다. 서버가 창 구성을 바꿔도
    /// 그대로 따라가므로 five_hour/seven_day를 하드코딩하는 것보다 안전하다.
    /// </summary>
    private ProviderUsage Parse(string json, string plan)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var windows = new List<UsageWindow>();

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var lim in limits.EnumerateArray())
            {
                string kind = lim.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                double pct = lim.TryGetProperty("percent", out var p) ? ReadNumber(p) : 0;
                DateTime? reset = lim.TryGetProperty("resets_at", out var r) ? ReadDate(r) : null;

                // 리셋 시각이 없으면 서버가 그 창을 내려주지 않은 것이다.
                // 0%로 받아들이면 "100% 남음"으로 잘못 보인다.
                if (reset is null) continue;

                windows.Add(new UsageWindow
                {
                    Kind = KindFor(kind),
                    RawLabel = LabelFor(kind, lim),
                    Percent = pct,
                    ResetsAt = reset,
                });
            }
        }

        // limits가 비어 있으면 최상위 필드로 대체한다.
        if (windows.Count == 0)
        {
            AddLegacy(root, "five_hour", WindowKind.Session, windows);
            AddLegacy(root, "seven_day", WindowKind.Weekly, windows);
        }

        return new ProviderUsage
        {
            Provider = Name,
            PlanName = plan,
            Windows = windows,
            LastUpdated = DateTime.Now,
        };
    }

    private static void AddLegacy(JsonElement root, string prop,
        WindowKind kind, List<UsageWindow> into)
    {
        if (!root.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Object) return;
        double pct = el.TryGetProperty("utilization", out var u) ? ReadNumber(u) : 0;
        DateTime? reset = el.TryGetProperty("resets_at", out var r) ? ReadDate(r) : null;

        // 위와 같은 이유로, 리셋 시각이 없는 창은 쓰지 않는다.
        if (reset is null) return;

        into.Add(new UsageWindow { Kind = kind, Percent = pct, ResetsAt = reset });
    }

    /// <summary>서버가 준 kind를 화면 배치용 분류로 옮긴다.</summary>
    private static WindowKind KindFor(string kind) => kind switch
    {
        "session" => WindowKind.Session,
        _ when kind.StartsWith("weekly", StringComparison.Ordinal) => WindowKind.Weekly,
        _ => WindowKind.Other,
    };

    private static string LabelFor(string kind, JsonElement lim)
    {
        // scope가 있으면 모델별 한도다. 예: weekly_opus -> "주간 Opus"
        string scope = lim.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? "" : "";

        string baseLabel = kind switch
        {
            "session" => Strings.Get("window.session"),
            "weekly_all" => Strings.Get("window.weekly"),
            "weekly_opus" => Strings.Get("window.weeklyOpus"),
            "weekly_sonnet" => Strings.Get("window.weeklySonnet"),
            _ => kind.Replace('_', ' '),
        };

        return string.IsNullOrEmpty(scope) ? baseLabel : $"{baseLabel} ({scope})";
    }

    private static double ReadNumber(JsonElement el) =>
        el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0;

    private static DateTime? ReadDate(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(el.GetString(), out var dto) ? dto.LocalDateTime : null;
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];
}
