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
/// Codex 사용량을 읽는다. ChatGPT 서버의 사용량 API를 먼저 묻고, 실패하면
/// 세션 로그(~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl)의 마지막 rate_limits로 물러선다.
/// 로그는 이 PC에서 Codex CLI를 돌릴 때만 갱신되므로 웹·Work·다른 PC에서 쓴 양이
/// 빠지고 며칠씩 낡을 수 있다. 그래서 웹 화면과 같은 값을 주는 API를 우선한다.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    /// <summary>가장 최근 세션 파일 몇 개까지 뒤져볼지. 마지막 파일이 짧으면 그 이전도 본다.</summary>
    private const int MaxFilesToScan = 8;

    /// <summary>파일 끝에서부터 읽어들일 최대 바이트. 세션 로그는 수십 MB가 될 수 있다.</summary>
    private const int TailBytes = 512 * 1024;

    /// <summary>ChatGPT 웹의 "사용 내역" 화면이 쓰는 것과 같은 값을 돌려준다. 비공식이다.</summary>
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    /// <summary>초기화권 목록과 사용. 공식 Codex 클라이언트가 쓰는 경로다.</summary>
    private const string ResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private const string ConsumeUrl = ResetCreditsUrl + "/consume";

    private readonly HttpClient _http;
    private readonly Func<string> _sessionsRoot;

    public string Name => "Codex";

    public CodexProvider(HttpClient http, Func<string> sessionsRoot)
    {
        _http = http;
        _sessionsRoot = sessionsRoot;
    }

    public async Task<ProviderUsage> FetchAsync(CancellationToken ct)
    {
        // 토큰 합계는 API가 주지 않으므로 로그는 API가 성공해도 읽는다.
        var local = await Task.Run(() => Fetch(ct), ct).ConfigureAwait(false);
        var remote = await FetchRemoteAsync(ct).ConfigureAwait(false);
        if (remote is null) return local;

        return new ProviderUsage
        {
            Provider = Name,
            PlanName = remote.PlanName.Length > 0 ? remote.PlanName : local.PlanName,
            Windows = remote.Windows,
            Tokens = local.Tokens,
            LastUpdated = remote.LastUpdated,
            ResetCredits = remote.ResetCredits,
        };
    }

    /// <summary>
    /// Codex CLI가 남긴 auth.json의 access_token으로 사용량 API를 부른다.
    /// 토큰은 읽기만 한다. 갱신해서 다시 쓰면 CLI와 경쟁하다 로그인이 풀릴 수 있고,
    /// 만료됐으면 로그로 물러서면 되므로 실패는 모두 null로 돌린다.
    /// </summary>
    private async Task<ProviderUsage?> FetchRemoteAsync(CancellationToken ct)
    {
        try
        {
            var auth = await ReadAuthAsync(ct).ConfigureAwait(false);
            if (auth is null) return null;

            using var req = NewRequest(HttpMethod.Get, UsageUrl, auth.Value);
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("rate_limit", out var rl) || rl.ValueKind != JsonValueKind.Object)
                return null;

            var windows = new List<UsageWindow>();
            AddRemoteWindow(rl, "primary_window", windows);
            AddRemoteWindow(rl, "secondary_window", windows);
            if (windows.Count == 0) return null;

            // 요약에 남은 개수가 있을 때만 목록을 부른다. 대부분은 0개라 왕복 하나를 아낀다.
            long available = root.TryGetProperty("rate_limit_reset_credits", out var rc) &&
                             rc.ValueKind == JsonValueKind.Object
                ? ReadLong(rc, "available_count") : 0;
            var credits = available > 0
                ? await FetchResetCreditsAsync(auth.Value, ct).ConfigureAwait(false)
                : Array.Empty<ResetCredit>();

            return new ProviderUsage
            {
                Provider = Name,
                PlanName = Capitalize(ReadString(root, "plan_type")),
                Windows = windows,
                LastUpdated = DateTime.Now,
                ResetCredits = credits,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 네트워크 오류, 타임아웃, 응답 형식 변경 모두 로그로 물러선다.
            return null;
        }
    }

    /// <summary>
    /// 쓸 수 있는 초기화권을 만료가 이른 순으로. 목록 조회가 실패해도 사용량은
    /// 보여야 하므로 빈 목록으로 돌린다.
    /// </summary>
    private async Task<IReadOnlyList<ResetCredit>> FetchResetCreditsAsync(Auth auth, CancellationToken ct)
    {
        try
        {
            using var req = NewRequest(HttpMethod.Get, ResetCreditsUrl, auth);
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return Array.Empty<ResetCredit>();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("credits", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<ResetCredit>();

            var list = new List<ResetCredit>();
            foreach (var c in arr.EnumerateArray())
            {
                // 사용 중이거나 이미 쓴 것도 섞여 올 수 있다. 누를 수 있는 것만 남긴다.
                if (ReadString(c, "status") != "available") continue;
                if (c.TryGetProperty("is_supported_by_plan", out var sp) && sp.ValueKind == JsonValueKind.False)
                    continue;

                string id = ReadString(c, "id");
                if (id.Length == 0) continue;

                DateTime? expires = DateTimeOffset.TryParse(ReadString(c, "expires_at"), out var dto)
                    ? dto.LocalDateTime : null;

                list.Add(new ResetCredit
                {
                    Id = id,
                    ResetType = ReadString(c, "reset_type"),
                    Title = ReadString(c, "title"),
                    ExpiresAt = expires,
                });
            }

            return list.OrderBy(c => c.ExpiresAt ?? DateTime.MaxValue).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<ResetCredit>();
        }
    }

    /// <summary>
    /// 초기화권 하나를 쓴다. 되돌릴 수 없으므로 자동 재시도는 하지 않는다.
    /// redeem_request_id는 서버가 중복 요청을 걸러내는 열쇠라 매번 새로 만든다.
    /// </summary>
    public async Task<ResetOutcome> ConsumeResetAsync(string creditId, CancellationToken ct = default)
    {
        try
        {
            var auth = await ReadAuthAsync(ct).ConfigureAwait(false);
            if (auth is null) return ResetOutcome.Failed;

            using var req = NewRequest(HttpMethod.Post, ConsumeUrl, auth.Value);
            req.Content = JsonContent(new Dictionary<string, string>
            {
                ["redeem_request_id"] = Guid.NewGuid().ToString(),
                ["credit_id"] = creditId,
            });

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return ResetOutcome.Failed;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return ReadString(doc.RootElement, "code") switch
            {
                // already_redeemed는 같은 요청이 이미 처리됐다는 뜻이라 성공과 같다.
                "reset" or "already_redeemed" => ResetOutcome.Reset,
                "nothing_to_reset" => ResetOutcome.NothingToReset,
                "no_credit" => ResetOutcome.NoCredit,
                _ => ResetOutcome.Failed,
            };
        }
        catch
        {
            return ResetOutcome.Failed;
        }
    }

    private readonly record struct Auth(string Token, string Account);

    /// <summary>
    /// Codex CLI가 남긴 auth.json에서 토큰을 읽는다. 읽기만 한다. 갱신해서 다시 쓰면
    /// CLI와 경쟁하다 로그인이 풀릴 수 있다.
    /// </summary>
    private async Task<Auth?> ReadAuthAsync(CancellationToken ct)
    {
        string? authPath = FindAuthFile();
        if (authPath is null) return null;

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(authPath, ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            return null;

        string token = ReadString(tokens, "access_token");
        return token.Length == 0 ? null : new Auth(token, ReadString(tokens, "account_id"));
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, Auth auth)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + auth.Token);
        if (auth.Account.Length > 0)
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.Account);
        return req;
    }

    private static StringContent JsonContent(Dictionary<string, string> body) =>
        new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    /// <summary>
    /// auth.json은 세션 폴더의 부모(~/.codex)에 있다. 세션 폴더를 직접 지정했어도
    /// 같은 구조일 가능성이 높아 먼저 보고, 없으면 기본 위치를 본다.
    /// </summary>
    private string? FindAuthFile()
    {
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_sessionsRoot()));
        if (parent is not null)
        {
            string near = Path.Combine(parent, "auth.json");
            if (File.Exists(near)) return near;
        }

        string fallback = Path.Combine(Path.GetDirectoryName(AppSettings.DefaultCodexPath)!, "auth.json");
        return File.Exists(fallback) ? fallback : null;
    }

    private static void AddRemoteWindow(JsonElement rateLimit, string key, List<UsageWindow> into)
    {
        // 5시간 한도가 적용되지 않는 기간에는 primary_window가 null로 온다.
        if (!rateLimit.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
            return;

        double pct = w.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number
            ? up.GetDouble() : 0;

        long seconds = ReadLong(w, "limit_window_seconds");
        int minutes = (int)(seconds / 60);

        DateTime? reset = null;
        if (w.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
            reset = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()).LocalDateTime;

        into.Add(new UsageWindow
        {
            Kind = KindFor(minutes),
            RawLabel = DescribeWindow(minutes),
            Percent = pct,
            ResetsAt = reset,
        });
    }

    private ProviderUsage Fetch(CancellationToken ct)
    {
        string root = _sessionsRoot();
        if (!Directory.Exists(root))
            return ProviderUsage.Unavailable(Name, Strings.Get("error.codexFolder"));

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxFilesToScan)
                .ToList();
        }
        catch (Exception ex)
        {
            return ProviderUsage.Unavailable(Name, Strings.Get("error.codexFolderRead", ex.Message));
        }

        if (files.Count == 0)
            return ProviderUsage.Unavailable(Name, Strings.Get("error.codexNoHistory"));

        ProviderUsage? tokenOnly = null;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var usage = TryReadLatest(file, ct);
            if (usage is null) continue;

            // 최신 token_count가 토큰 합계만 담고 rate_limits를 생략한 경우가
            // 있다. 그것만 보고 탐색을 끝내면 더 이전 파일에 남아 있는 마지막
            // quota 기록을 놓치므로, 창이 있는 기록을 찾을 때까지 계속 본다.
            if (usage.Windows.Count > 0) return usage;
            tokenOnly ??= usage;
        }

        return tokenOnly ?? ProviderUsage.Unavailable(Name, Strings.Get("error.codexNoUsage"));
    }

    /// <summary>파일 끝부분만 읽어 가장 마지막 rate_limits / token_count 이벤트를 찾는다.</summary>
    private ProviderUsage? TryReadLatest(FileInfo file, CancellationToken ct)
    {
        string[] lines;
        try
        {
            lines = ReadTailLines(file);
        }
        catch
        {
            return null;
        }

        // 뒤에서부터 훑어 가장 최신 이벤트를 먼저 만난다.
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();

            string line = lines[i];
            // 값싼 사전 필터. JSON 파싱은 후보에만 수행한다.
            if (line.Length < 2 || line.IndexOf("token_count", StringComparison.Ordinal) < 0)
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("payload", out var payload)) continue;

                var result = ParsePayload(payload, ReadTimestamp(doc.RootElement, file));
                if (result is not null) return result;
            }
            catch (JsonException)
            {
                // 마지막 줄이 잘렸거나 tail 경계에서 잘린 줄. 건너뛴다.
            }
        }

        return null;
    }

    private ProviderUsage? ParsePayload(JsonElement payload, DateTime timestamp)
    {
        var windows = new List<UsageWindow>();
        string plan = "";

        if (payload.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
        {
            if (rl.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String)
                plan = Capitalize(pt.GetString() ?? "");

            AddWindow(rl, "primary", windows);
            AddWindow(rl, "secondary", windows);
        }

        TokenTotals? tokens = null;
        if (payload.TryGetProperty("info", out var info) &&
            info.TryGetProperty("total_token_usage", out var ttu))
        {
            tokens = new TokenTotals
            {
                Input = ReadLong(ttu, "input_tokens"),
                Output = ReadLong(ttu, "output_tokens"),
                CacheRead = ReadLong(ttu, "cached_input_tokens"),
                CacheWrite = ReadLong(ttu, "cache_write_input_tokens"),
            };
        }

        if (windows.Count == 0 && tokens is null) return null;

        return new ProviderUsage
        {
            Provider = Name,
            PlanName = plan,
            Windows = windows,
            Tokens = tokens,
            LastUpdated = timestamp,
        };
    }

    private static void AddWindow(JsonElement rateLimits, string key, List<UsageWindow> into)
    {
        if (!rateLimits.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
            return;

        double pct = w.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number
            ? up.GetDouble() : 0;

        int minutes = w.TryGetProperty("window_minutes", out var wm) && wm.ValueKind == JsonValueKind.Number
            ? wm.GetInt32() : 0;

        DateTime? reset = null;
        if (w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
            reset = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()).LocalDateTime;

        var kind = KindFor(minutes);

        // Codex가 5시간 한도를 적용하지 않거나 아직 창을 시작하지 않은 경우에는
        // used_percent가 있어도 resets_at이 빠질 수 있다. 이 값을 실제 세션 한도로
        // 그리면 (특히 잔여량 모드에서) 게이지가 찬 것처럼 보이므로 빈 칸으로 둔다.
        if (kind == WindowKind.Session && reset is null)
            return;

        into.Add(new UsageWindow
        {
            Kind = kind,
            RawLabel = DescribeWindow(minutes),
            Percent = pct,
            ResetsAt = reset,
        });
    }

    /// <summary>
    /// 창 길이로 종류를 가른다. 하루 미만이면 세션, 그 이상이면 주간으로 본다.
    /// </summary>
    private static WindowKind KindFor(int minutes) => minutes switch
    {
        <= 0 => WindowKind.Other,
        < 1440 => WindowKind.Session,
        _ => WindowKind.Weekly,
    };

    /// <summary>window_minutes를 사람이 읽는 이름으로. 10080분 = 주간.</summary>
    private static string DescribeWindow(int minutes) => minutes switch
    {
        0 => Strings.Get("window.usage"),
        < 60 => Strings.Get("age.minutes", minutes),
        < 1440 => Strings.Get("age.hours", minutes / 60),
        10080 => Strings.Get("window.weekly"),
        < 10080 => Strings.Get("age.days", minutes / 1440),
        _ => Strings.Get("window.weekly"),
    };

    /// <summary>파일 끝 TailBytes만 읽는다. 첫 줄은 잘렸을 수 있으므로 버린다.</summary>
    private static string[] ReadTailLines(FileInfo file)
    {
        using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        bool truncated = fs.Length > TailBytes;
        if (truncated) fs.Seek(-TailBytes, SeekOrigin.End);

        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
        string content = reader.ReadToEnd();

        var lines = content.Split('\n');
        // 앞부분에서 잘린 줄을 제거한다.
        return truncated && lines.Length > 1 ? lines[1..] : lines;
    }

    private static DateTime ReadTimestamp(JsonElement root, FileInfo fallback)
    {
        if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(ts.GetString(), out var dto))
            return dto.LocalDateTime;

        return fallback.LastWriteTime;
    }

    private static long ReadLong(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static string ReadString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];
}
