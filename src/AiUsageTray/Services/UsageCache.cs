using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

/// <summary>
/// 마지막으로 성공한 조회 결과를 디스크에 남긴다.
/// 앱을 다시 켰을 때 서버가 응답하지 않아도 직전 수치를 보여줄 수 있다.
/// </summary>
public static class UsageCache
{
    private static string CacheFile =>
        Path.Combine(AppSettings.SettingsDirectory, "last-usage.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(IEnumerable<ProviderUsage> usages)
    {
        try
        {
            var payload = new List<Entry>();
            foreach (var u in usages)
            {
                // 성공한 값만 남긴다. 낡은 값을 다시 낡은 채로 저장하지 않는다.
                if (u.Error is not null) continue;

                payload.Add(new Entry
                {
                    Provider = u.Provider,
                    PlanName = u.PlanName,
                    LastUpdated = u.LastUpdated ?? DateTime.Now,
                    Tokens = u.Tokens is null ? null : new TokenEntry
                    {
                        Input = u.Tokens.Input,
                        Output = u.Tokens.Output,
                        CacheRead = u.Tokens.CacheRead,
                        CacheWrite = u.Tokens.CacheWrite,
                    },
                    Windows = new List<WindowEntry>(ToEntries(u.Windows)),
                });
            }

            if (payload.Count == 0) return;

            Directory.CreateDirectory(AppSettings.SettingsDirectory);

            string tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, Options));
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch
        {
            // 캐시는 편의 기능이다. 실패해도 앱 동작에는 지장이 없다.
        }
    }

    public static Dictionary<string, ProviderUsage> Load()
    {
        var result = new Dictionary<string, ProviderUsage>();

        try
        {
            if (!File.Exists(CacheFile)) return result;

            var entries = JsonSerializer.Deserialize<List<Entry>>(
                File.ReadAllText(CacheFile), Options);

            if (entries is null) return result;

            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.Provider)) continue;

                var windows = new List<UsageWindow>();
                foreach (var w in e.Windows)
                {
                    windows.Add(new UsageWindow
                    {
                        Kind = w.Kind,
                        RawLabel = w.RawLabel,
                        Percent = w.Percent,
                        ResetsAt = w.ResetsAt,
                    });
                }

                result[e.Provider] = new ProviderUsage
                {
                    Provider = e.Provider,
                    PlanName = e.PlanName,
                    Windows = windows,
                    Tokens = e.Tokens is null ? null : new TokenTotals
                    {
                        Input = e.Tokens.Input,
                        Output = e.Tokens.Output,
                        CacheRead = e.Tokens.CacheRead,
                        CacheWrite = e.Tokens.CacheWrite,
                    },
                    LastUpdated = e.LastUpdated,
                };
            }
        }
        catch
        {
            // 형식이 깨졌으면 캐시가 없는 것으로 친다.
        }

        return result;
    }

    /// <summary>
    /// 자동 메시지가 성공하면 서버 재조회 전이라도 로컬 구간을 즉시 시작한다.
    /// 사용률은 최소 요청 하나라 화면 정밀도보다 새 구간이 열렸다는 사실이 중요해
    /// 0으로 두고, 다음 동기화에서 서버 값으로 교체한다.
    /// </summary>
    public static Dictionary<string, ProviderUsage> ApplyWindowStarted(
        string provider, WindowKind kind, DateTime startedAt)
    {
        var cached = Load();
        if (!cached.TryGetValue(provider, out var usage))
        {
            usage = new ProviderUsage
            {
                Provider = provider,
                Windows = new[] { new UsageWindow { Kind = kind } },
            };
        }

        var windows = usage.Windows.Select(w => w.Kind == kind
            ? new UsageWindow
            {
                Kind = w.Kind,
                RawLabel = w.RawLabel,
                Percent = 0,
                ResetsAt = startedAt + (kind == WindowKind.Weekly
                    ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5)),
            }
            : w).ToList();

        cached[provider] = new ProviderUsage
        {
            Provider = usage.Provider,
            PlanName = usage.PlanName,
            Windows = windows,
            Tokens = usage.Tokens,
            LastUpdated = startedAt,
        };
        Save(cached.Values);
        return cached;
    }

    private static IEnumerable<WindowEntry> ToEntries(IReadOnlyList<UsageWindow> windows)
    {
        foreach (var w in windows)
            yield return new WindowEntry { Kind = w.Kind, RawLabel = w.RawLabel, Percent = w.Percent, ResetsAt = w.ResetsAt };
    }

    // 직렬화 전용 형태. 도메인 모델은 init 전용이라 그대로 쓰기 어렵다.
    private sealed class Entry
    {
        public string Provider { get; set; } = "";
        public string PlanName { get; set; } = "";
        public DateTime LastUpdated { get; set; }
        public TokenEntry? Tokens { get; set; }
        public List<WindowEntry> Windows { get; set; } = new();
    }

    private sealed class TokenEntry
    {
        public long Input { get; set; }
        public long Output { get; set; }
        public long CacheRead { get; set; }
        public long CacheWrite { get; set; }
    }

    private sealed class WindowEntry
    {
        /// <summary>번역된 이름 대신 분류를 저장한다. 언어가 바뀌어도 따라온다.</summary>
        public WindowKind Kind { get; set; }
        public string RawLabel { get; set; } = "";
        public double Percent { get; set; }
        public DateTime? ResetsAt { get; set; }
    }
}
