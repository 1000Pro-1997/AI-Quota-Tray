using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

/// <summary>
/// Claude Code의 statusLine 입력에서 구독 한도를 받아 앱 캐시에 저장한다.
/// 기존 statusLine은 사용자가 만든 설정일 수 있으므로 절대 덮어쓰지 않는다.
/// </summary>
public static class ClaudeStatusLineBridge
{
    private const string BridgeFileName = "claude-statusline-bridge.ps1";
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    public static string CacheFile => Path.Combine(AppSettings.SettingsDirectory, "claude-statusline.json");

    private static string ClaudeSettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static string BridgeFile => Path.Combine(AppSettings.SettingsDirectory, BridgeFileName);

    /// <summary>일반 앱 시작 시 비어 있는 Claude statusLine에 수신 명령을 등록한다.</summary>
    public static void EnsureInstalled()
    {
        try
        {
            string settingsPath = ClaudeSettingsFile;
            string? dir = Path.GetDirectoryName(settingsPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            File.WriteAllText(BridgeFile, BuildBridgeScript());

            JsonObject root;
            if (File.Exists(settingsPath))
            {
                root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            if (root["statusLine"] is JsonObject existing)
            {
                string command = existing["command"]?.GetValue<string>() ?? "";
                if (!command.Contains(BridgeFileName, StringComparison.OrdinalIgnoreCase)) return;

                // 우리 설정이면 앱 데이터 경로가 바뀌었을 때 명령만 최신으로 고친다.
                string updated = BuildCommand();
                if (string.Equals(command, updated, StringComparison.Ordinal)) return;
                existing["command"] = updated;
            }
            else if (root["statusLine"] is not null)
            {
                return;
            }
            else
            {
                root["statusLine"] = new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = BuildCommand(),
                };
            }

            var opts = new JsonSerializerOptions { WriteIndented = true };
            string tmp = settingsPath + ".ai-quota-tray.tmp";
            File.WriteAllText(tmp, root.ToJsonString(opts));
            File.Move(tmp, settingsPath, overwrite: true);
        }
        catch
        {
            // 자동 연동 실패는 OAuth 조회를 막지 않는다.
        }
    }

    public static ProviderUsage? TryReadFresh(string plan)
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
            var root = doc.RootElement;

            if (!root.TryGetProperty("captured_at", out var capturedEl) ||
                !DateTimeOffset.TryParse(capturedEl.GetString(), out var captured) ||
                DateTimeOffset.UtcNow - captured > MaxAge) return null;

            var windows = new List<UsageWindow>();
            AddWindow(root, "five_hour", WindowKind.Session, windows);
            AddWindow(root, "seven_day", WindowKind.Weekly, windows);
            if (windows.Count == 0) return null;

            return new ProviderUsage
            {
                Provider = "Claude",
                PlanName = plan,
                Windows = windows,
                LastUpdated = captured.LocalDateTime,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string BuildCommand() =>
        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{BridgeFile}\"";

    private static string BuildBridgeScript()
    {
        string cache = CacheFile.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $raw = [Console]::In.ReadToEnd()
            if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
            $inputData = $raw | ConvertFrom-Json
            $limits = $inputData.rate_limits
            if ($null -eq $limits) { exit 0 }

            $result = [ordered]@{ captured_at = [DateTimeOffset]::UtcNow.ToString('O') }
            foreach ($name in @('five_hour', 'seven_day')) {
                $window = $limits.$name
                if ($null -ne $window -and $null -ne $window.used_percentage -and $null -ne $window.resets_at) {
                    $result[$name] = [ordered]@{
                        used_percentage = [double]$window.used_percentage
                        resets_at = [long]$window.resets_at
                    }
                }
            }
            if ($result.Count -le 1) { exit 0 }

            $cache = '{{cache}}'
            $tmp = $cache + '.tmp'
            $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $tmp -Encoding UTF8
            Move-Item -LiteralPath $tmp -Destination $cache -Force
            exit 0
            """;
    }

    private static void AddWindow(JsonElement root, string name, WindowKind kind, List<UsageWindow> into)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return;
        if (!window.TryGetProperty("used_percentage", out var percent) || percent.ValueKind != JsonValueKind.Number) return;
        if (!window.TryGetProperty("resets_at", out var reset) || reset.ValueKind != JsonValueKind.Number) return;

        into.Add(new UsageWindow
        {
            Kind = kind,
            Percent = percent.GetDouble(),
            ResetsAt = DateTimeOffset.FromUnixTimeSeconds(reset.GetInt64()).LocalDateTime,
        });
    }
}
