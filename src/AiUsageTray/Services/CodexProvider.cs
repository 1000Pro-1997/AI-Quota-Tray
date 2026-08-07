using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

/// <summary>
/// Codex 세션 로그(~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl)에서 사용량을 읽는다.
/// 로그에는 서버가 내려준 rate_limits가 그대로 들어있어 추정이 필요 없다.
/// 네트워크 호출도, 토큰 접근도 하지 않는다.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    /// <summary>가장 최근 세션 파일 몇 개까지 뒤져볼지. 마지막 파일이 짧으면 그 이전도 본다.</summary>
    private const int MaxFilesToScan = 8;

    /// <summary>파일 끝에서부터 읽어들일 최대 바이트. 세션 로그는 수십 MB가 될 수 있다.</summary>
    private const int TailBytes = 512 * 1024;

    private readonly Func<string> _sessionsRoot;

    public string Name => "Codex";

    public CodexProvider(Func<string> sessionsRoot) => _sessionsRoot = sessionsRoot;

    public Task<ProviderUsage> FetchAsync(CancellationToken ct) =>
        Task.Run(() => Fetch(ct), ct);

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

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var usage = TryReadLatest(file, ct);
            if (usage is not null) return usage;
        }

        return ProviderUsage.Unavailable(Name, Strings.Get("error.codexNoUsage"));
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

        into.Add(new UsageWindow
        {
            Kind = KindFor(minutes),
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

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];
}
