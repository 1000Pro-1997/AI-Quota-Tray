using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>이보다 오래된 기록은 쓰지 않는다. 낡은 수치는 오해를 부른다.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

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
                if (DateTime.Now - e.LastUpdated > MaxAge) continue;
                if (string.IsNullOrEmpty(e.Provider)) continue;

                var windows = new List<UsageWindow>();
                foreach (var w in e.Windows)
                {
                    windows.Add(new UsageWindow
                    {
                        Label = w.Label,
                        Percent = w.Percent,
                        ResetsAt = w.ResetsAt,
                    });
                }

                result[e.Provider] = new ProviderUsage
                {
                    Provider = e.Provider,
                    PlanName = e.PlanName,
                    Windows = windows,
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

    private static IEnumerable<WindowEntry> ToEntries(IReadOnlyList<UsageWindow> windows)
    {
        foreach (var w in windows)
            yield return new WindowEntry { Label = w.Label, Percent = w.Percent, ResetsAt = w.ResetsAt };
    }

    // 직렬화 전용 형태. 도메인 모델은 init 전용이라 그대로 쓰기 어렵다.
    private sealed class Entry
    {
        public string Provider { get; set; } = "";
        public string PlanName { get; set; } = "";
        public DateTime LastUpdated { get; set; }
        public List<WindowEntry> Windows { get; set; } = new();
    }

    private sealed class WindowEntry
    {
        public string Label { get; set; } = "";
        public double Percent { get; set; }
        public DateTime? ResetsAt { get; set; }
    }
}
