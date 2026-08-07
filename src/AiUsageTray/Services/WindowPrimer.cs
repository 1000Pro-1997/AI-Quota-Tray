using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

/// <summary>
/// 5시간 창이 끝나는 시각에 최소 요청을 한 번 보내 다음 창을 같은 시간축에서 시작한다.
/// 주간 한도는 계정 고정 시각에 초기화되므로 건드리지 않는다.
/// </summary>
public sealed class WindowPrimer : IDisposable
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromHours(5);
    private static readonly TimeSpan TriggerDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxLate = TimeSpan.FromMinutes(10);

    private readonly AppSettings _settings;
    private readonly Timer _timer;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTime> _next = new(StringComparer.OrdinalIgnoreCase);
    private int _running;

    private static string StateFile => Path.Combine(AppSettings.SettingsDirectory, "window-primer.json");

    public WindowPrimer(AppSettings settings)
    {
        _settings = settings;
        LoadState();
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
    }

    /// <summary>서버가 알려준 현재 5시간 창의 끝을 다음 자동 시작 기준으로 삼는다.</summary>
    public void Observe(IReadOnlyList<ProviderUsage> usages)
    {
        if (!_settings.KeepFiveHourWindowsAligned) return;

        bool changed = false;
        lock (_sync)
        {
            foreach (var usage in usages)
            {
                var reset = usage.Windows
                    .Where(w => w.Kind == WindowKind.Session && w.ResetsAt is not null)
                    .Select(w => w.ResetsAt!.Value)
                    .OrderBy(v => v)
                    .FirstOrDefault();

                if (reset == default) continue;
                if (!_next.TryGetValue(usage.Provider, out var current) || reset > current)
                {
                    _next[usage.Provider] = reset;
                    changed = true;
                }
            }
        }
        if (changed) SaveState();
    }

    public void SettingsChanged()
    {
        if (_settings.KeepFiveHourWindowsAligned) return;
        lock (_sync) _next.Clear();
        SaveState();
    }

    private async Task TickAsync()
    {
        if (!_settings.KeepFiveHourWindowsAligned) return;
        if (Interlocked.Exchange(ref _running, 1) != 0) return;

        try
        {
            var now = DateTime.Now;
            var due = new List<string>();
            bool changed = false;

            lock (_sync)
            {
                foreach (string provider in _next.Keys.ToArray())
                {
                    DateTime scheduled = _next[provider];
                    DateTime triggerAt = scheduled + TriggerDelay;
                    if (now < triggerAt) continue;

                    if (now - triggerAt <= MaxLate && ProviderEnabled(provider))
                        due.Add(provider);

                    // 절전 등으로 놓쳤으면 늦게 시작해 시간축을 밀지 않고 다음 5시간을 기다린다.
                    do { scheduled += WindowLength; }
                    while (scheduled + TriggerDelay <= now);

                    _next[provider] = scheduled;
                    changed = true;
                }
            }

            if (changed) SaveState();
            foreach (string provider in due)
                await PrimeAsync(provider).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private bool ProviderEnabled(string provider) => provider switch
    {
        "Claude" => _settings.ClaudeEnabled,
        "Codex" => _settings.CodexEnabled,
        _ => false,
    };

    private static async Task PrimeAsync(string provider)
    {
        try
        {
            string? executable = FindExecutable(provider == "Claude" ? "claude.exe" : "codex.exe");
            if (executable is null) return;

            Directory.CreateDirectory(AppSettings.PrimerWorkingDirectory);
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppSettings.PrimerWorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (provider == "Claude")
            {
                foreach (string arg in new[]
                {
                    "--bg", "--safe-mode", "--no-chrome", "--tools", "", "--effort", "low",
                    "--permission-mode", "plan", "--name", "AI Quota Tray window start",
                    "Reply with OK only. Do not use tools."
                }) start.ArgumentList.Add(arg);
            }
            else
            {
                foreach (string arg in new[]
                {
                    "exec", "--ephemeral", "--ignore-user-config", "--skip-git-repo-check",
                    "--sandbox", "read-only", "-c", "model_reasoning_effort=\"low\"",
                    "Reply with OK only. Do not use tools."
                }) start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
            if (process is null) return;
            Task stdout = process.StandardOutput.ReadToEndAsync();
            Task stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch
        {
            // 자동 시작 실패가 트레이 앱이나 다음 예약을 방해하면 안 된다.
        }
    }

    private static string? FindExecutable(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var state = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(StateFile));
            if (state is null) return;
            foreach (var pair in state) _next[pair.Key] = pair.Value;
        }
        catch { }
    }

    private void SaveState()
    {
        try
        {
            Dictionary<string, DateTime> snapshot;
            lock (_sync) snapshot = new Dictionary<string, DateTime>(_next);
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            string tmp = StateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StateFile, overwrite: true);
        }
        catch { }
    }

    public void Dispose() => _timer.Dispose();
}
