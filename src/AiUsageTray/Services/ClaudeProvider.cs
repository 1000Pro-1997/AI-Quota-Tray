using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// usage 엔드포인트는 User-Agent가 claude-code/버전이 아니면 훨씬 빡빡한 한도를
    /// 적용해 429가 몇 시간씩 이어진다. 그래서 설치된 Claude Code 버전을 흉내 낸다.
    /// 버전 폴더를 못 찾으면 알려진 버전을 쓴다. 숫자 자체는 검사하지 않는 것으로 보인다.
    /// </summary>
    private static readonly Lazy<string> ClaudeCodeUserAgent = new(() =>
        "claude-code/" + (DetectClaudeCodeVersion() ?? "2.1.281"));

    private readonly HttpClient _http;
    private readonly Func<string> _credentialsPath;

    private static string BackoffFile =>
        Path.Combine(AppSettings.SettingsDirectory, "claude-usage-backoff.json");

    /// <summary>429를 받으면 이 시각까지는 서버를 다시 부르지 않는다.</summary>
    private DateTime _blockedUntil = DateTime.MinValue;

    /// <summary>
    /// 마지막 OAuth 응답의 추가 사용량. statusLine에는 이 값이 없어서,
    /// 그쪽 값을 쓸 때도 사라지지 않도록 덧붙이는 데 쓴다.
    /// </summary>
    private UsageWindow? _lastExtra;

    public string Name => "Claude";

    public ClaudeProvider(HttpClient http, Func<string> credentialsPath)
    {
        _http = http;
        _credentialsPath = credentialsPath;
        _blockedUntil = LoadBlockedUntil();
    }

    public async Task<ProviderUsage> FetchAsync(CancellationToken ct)
    {
        string path = _credentialsPath();
        string token = "", plan = "";
        long expiresAt = 0;

        if (File.Exists(path))
        {
            try
            {
                (token, plan, expiresAt) = ReadCredentials(path);
            }
            catch (Exception ex)
            {
                if (ClaudeStatusLineBridge.TryReadFresh("") is { } cached) return cached;
                if (OmpCredentials.ReadAnthropic() is null)
                    return ProviderUsage.Unavailable(Name, Strings.Get("error.credentialsUnreadable", ex.Message));
            }
        }

        // Claude Code가 방금 실제 응답에서 받은 값이 가장 최신이다.
        // 없거나 오래됐으면 아래의 기존 OAuth usage 조회를 그대로 사용한다.
        if (ClaudeStatusLineBridge.TryReadFresh(plan) is { } bridged)
            return WithExtra(bridged);

        // Claude Code를 안 켜고 omp로만 쓰면 ~/.claude 토큰은 만료된 채 남는다.
        // 그 토큰으로 부르면 401만 받으니 omp가 갱신해 둔 토큰을 빌린다.
        bool expired = expiresAt > 0 && expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (string.IsNullOrEmpty(token) || expired)
            token = OmpCredentials.ReadAnthropic() ?? token;

        // Claude Code는 로그아웃할 때 파일을 지우지 않고 토큰만 비워 둔다.
        if (string.IsNullOrEmpty(token))
            return ProviderUsage.SignedOut(Name);

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
            // 요청에 넣은 값이 HttpClient 기본 User-Agent보다 우선한다.
            req.Headers.TryAddWithoutValidation("User-Agent", ClaudeCodeUserAgent.Value);

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
                SaveBlockedUntil(_blockedUntil);

                return ProviderUsage.Unavailable(Name,
                    Strings.Get("error.rateLimited", (int)retry.TotalSeconds));
            }

            if (!res.IsSuccessStatusCode)
                return ProviderUsage.Unavailable(Name, Strings.Get("error.httpFailed", (int)res.StatusCode));

            // 성공했으니 백오프를 푼다.
            _blockedUntil = DateTime.MinValue;
            ClearBlockedUntil();

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

    private static (string token, string plan, long expiresAt) ReadCredentials(string path)
    {
        // Claude Code가 동시에 쓸 수 있으므로 공유 읽기로 연다.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = JsonDocument.Parse(fs);

        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            return ("", "", 0);

        string token = oauth.TryGetProperty("accessToken", out var t) ? t.GetString() ?? "" : "";
        string plan = oauth.TryGetProperty("subscriptionType", out var s) ? s.GetString() ?? "" : "";
        long expiresAt = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt64() : 0;
        return (token, Capitalize(plan), expiresAt);
    }

    /// <summary>
    /// 설정 창 표시용. 파일이 있어도 로그아웃하면 토큰이 비어 있으므로
    /// 파일 존재만으로 "찾았다"고 하면 안 된다.
    /// </summary>
    public static bool HasToken(string path)
    {
        try
        {
            return File.Exists(path) && !string.IsNullOrEmpty(ReadCredentials(path).token);
        }
        catch
        {
            return false;
        }
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
                    Kind = KindFor(kind, lim),
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

        // 추가 사용량은 limits에 들어오지 않고 별도 필드로만 온다.
        _lastExtra = ReadExtra(root);
        if (_lastExtra is not null) windows.Add(_lastExtra);

        return new ProviderUsage
        {
            Provider = Name,
            PlanName = plan,
            Windows = windows,
            LastUpdated = DateTime.Now,
        };
    }

    /// <summary>
    /// 켜 둔 경우에만 보여준다. 꺼 둔 계정에 바를 그리면 쓸 수 없는 한도가
    /// 있는 것처럼 보인다. 리셋 시각은 응답에 없다 (청구 주기 기준이라
    /// 달력 월말로 추정하면 틀릴 수 있어 비워 둔다).
    /// </summary>
    private static UsageWindow? ReadExtra(JsonElement root)
    {
        if (!root.TryGetProperty("extra_usage", out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        if (!el.TryGetProperty("is_enabled", out var on) || on.ValueKind != JsonValueKind.True)
            return null;
        if (!el.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number)
            return null;

        return new UsageWindow { Kind = WindowKind.Extra, Percent = u.GetDouble() };
    }

    private ProviderUsage WithExtra(ProviderUsage u)
    {
        if (_lastExtra is null) return u;
        return new ProviderUsage
        {
            Provider = u.Provider,
            PlanName = u.PlanName,
            Windows = u.Windows.Append(_lastExtra).ToList(),
            Tokens = u.Tokens,
            LastUpdated = u.LastUpdated,
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

    /// <summary>
    /// 서버가 준 kind를 화면 배치용 분류로 옮긴다. Weekly 칸은 전체 주간 한도
    /// 하나만 차지한다. 모델별 주간 한도(weekly_scoped·weekly_opus 등, scope가 붙은 것)까지
    /// Weekly로 넣으면 같은 "주간" 줄이 둘 생기고, 위젯바·트레이가 FirstOrDefault로
    /// 고르는 주간 값도 순서에 따라 바뀐다.
    /// </summary>
    private static WindowKind KindFor(string kind, JsonElement lim)
    {
        if (kind == "session") return WindowKind.Session;
        if (!kind.StartsWith("weekly", StringComparison.Ordinal)) return WindowKind.Other;

        bool scoped = lim.TryGetProperty("scope", out var s)
            && s.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        return (kind == "weekly_all" || kind == "weekly") && !scoped
            ? WindowKind.Weekly : WindowKind.ModelWeekly;
    }

    /// <summary>
    /// 모델별 주간 한도는 모델 이름만 남긴다. "주간" 부분은 화면에서 현재 언어로 붙인다.
    /// scope는 {"model":{"display_name":"Fable"}} 처럼 객체로 온다(2026-10 실측).
    /// 이름이 없으면 weekly_opus 같은 kind 꼬리를 쓴다.
    /// </summary>
    private static string LabelFor(string kind, JsonElement lim)
    {
        if (KindFor(kind, lim) != WindowKind.ModelWeekly) return kind.Replace('_', ' ');

        if (lim.TryGetProperty("scope", out var scope))
        {
            if (scope.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(scope.GetString()))
                return scope.GetString()!;
            if (scope.ValueKind == JsonValueKind.Object &&
                scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object &&
                model.TryGetProperty("display_name", out var name) && name.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(name.GetString()))
                return name.GetString()!;
        }

        string tail = kind["weekly".Length..].TrimStart('_');
        return tail is "" or "scoped" ? "" : Capitalize(tail);
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

    /// <summary>
    /// 네이티브 설치판은 ~/.local/share/claude/versions/2.1.281 처럼 버전마다 폴더(파일)를
    /// 남긴다. CLI를 실행하면 창이 번쩍이고 느려서 이름만 읽는다.
    /// </summary>
    private static string? DetectClaudeCodeVersion()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "claude", "versions");
            if (!Directory.Exists(dir)) return null;

            return Directory.EnumerateFileSystemEntries(dir)
                .Select(Path.GetFileName)
                .Select(name => Version.TryParse(name, out var v) ? v : null)
                .Where(v => v is not null)
                .Max()?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>앱을 다시 켜도 서버가 지정한 재시도 시각을 어기지 않는다.</summary>
    private static DateTime LoadBlockedUntil()
    {
        try
        {
            if (!File.Exists(BackoffFile)) return DateTime.MinValue;
            using var doc = JsonDocument.Parse(File.ReadAllText(BackoffFile));
            // 예전 User-Agent로 받은 백오프는 지금의 한도 버킷과 무관하다. 표식이 없으면 버린다.
            if (!doc.RootElement.TryGetProperty("blocked_until", out var value) ||
                !doc.RootElement.TryGetProperty("user_agent", out var ua) ||
                ua.GetString() != ClaudeCodeUserAgent.Value ||
                !DateTimeOffset.TryParse(value.GetString(), out var parsed))
                return DateTime.MinValue;

            DateTime local = parsed.LocalDateTime;
            if (local <= DateTime.Now)
            {
                ClearBlockedUntil();
                return DateTime.MinValue;
            }
            return local;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static void SaveBlockedUntil(DateTime blockedUntil)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            string tmp = BackoffFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new
            {
                blocked_until = new DateTimeOffset(blockedUntil).ToString("O"),
                user_agent = ClaudeCodeUserAgent.Value,
            }));
            File.Move(tmp, BackoffFile, overwrite: true);
        }
        catch
        {
            // 저장에 실패해도 현재 실행의 메모리 백오프는 계속 지킨다.
        }
    }

    private static void ClearBlockedUntil()
    {
        try { if (File.Exists(BackoffFile)) File.Delete(BackoffFile); }
        catch { }
    }
}
