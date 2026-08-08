using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

public enum TimeDisplayMode { Remaining, ResetAt }

public static partial class TimeDisplayFormatter
{
    private sealed record Part(string Token, string Suffix);

    public static string Format(UsageWindow window, AppSettings settings, bool overlay = false)
    {
        bool weekly = window.Kind == WindowKind.Weekly;
        string pattern = weekly ? settings.WeeklyTimeFormat : settings.SessionTimeFormat;
        int maxParts = Math.Clamp(weekly
            ? (overlay ? settings.WeeklyOverlayTimeMaxParts : settings.WeeklyTimeMaxParts)
            : (overlay ? settings.SessionOverlayTimeMaxParts : settings.SessionTimeMaxParts), 1, 5);
        var mode = weekly ? settings.WeeklyTimeDisplayMode : settings.SessionTimeDisplayMode;
        return Format(window.ResetsAt, pattern, maxParts, mode, DateTime.Now);
    }

    public static string Format(DateTime? resetAt, string pattern, int maxParts,
        TimeDisplayMode mode, DateTime now)
    {
        if (resetAt is null) return "";
        var parts = Parse(pattern);
        if (parts.Count == 0) return "";

        var values = mode == TimeDisplayMode.ResetAt
            ? AbsoluteValues(resetAt.Value, now)
            : RemainingValues(resetAt.Value - now, parts.Any(p => p.Token == "MM"));

        int start = resetAt.Value <= now
            ? Math.Max(0, parts.Count - maxParts)
            : FindStart(parts, values, mode, resetAt.Value, now);
        return string.Join(" ", parts.Skip(start).Take(Math.Clamp(maxParts, 1, 5))
            .Select(p => values[p.Token] + p.Suffix));
    }

    private static List<Part> Parse(string pattern)
    {
        var result = new List<Part>();
        foreach (Match match in TokenRegex().Matches(pattern ?? ""))
            result.Add(new Part(match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : ""));
        return result;
    }

    private static Dictionary<string, int> RemainingValues(TimeSpan span, bool includeMonths)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        int totalDays = (int)span.TotalDays;
        return new()
        {
            ["MM"] = includeMonths ? totalDays / 30 : 0,
            ["dd"] = includeMonths ? totalDays % 30 : totalDays,
            ["hh"] = span.Hours,
            ["mm"] = span.Minutes,
            ["ss"] = span.Seconds,
        };
    }

    private static Dictionary<string, int> AbsoluteValues(DateTime target, DateTime now) => new()
    {
        ["MM"] = target.Month,
        ["dd"] = target.Day,
        ["hh"] = target.Hour,
        ["mm"] = target.Minute,
        ["ss"] = target.Second,
    };

    private static int FindStart(IReadOnlyList<Part> parts, IReadOnlyDictionary<string, int> values,
        TimeDisplayMode mode, DateTime target, DateTime now)
    {
        if (mode == TimeDisplayMode.ResetAt)
        {
            string token = target.Year != now.Year || target.Month != now.Month ? "MM"
                : target.Date != now.Date ? "dd"
                : target.Hour != now.Hour ? "hh"
                : target.Minute != now.Minute ? "mm" : "ss";
            int exact = parts.ToList().FindIndex(p => p.Token == token);
            if (exact >= 0) return exact;
            if (token == "MM")
            {
                int day = parts.ToList().FindIndex(p => p.Token == "dd");
                if (day >= 0) return day;
            }
        }

        for (int i = 0; i < parts.Count; i++)
            if (values[parts[i].Token] != 0) return i;
        return Math.Max(0, parts.Count - 1);
    }

    [GeneratedRegex("(MM|dd|hh|mm|ss)(?:\\\"([^\\\"]*)\\\")?")]
    private static partial Regex TokenRegex();
}
