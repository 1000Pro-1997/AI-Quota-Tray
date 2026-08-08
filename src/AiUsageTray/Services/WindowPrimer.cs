using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

/// <summary>5시간 창을 같은 시간축에서 시작하며, 주간 한도는 건드리지 않는다.</summary>
public sealed class WindowPrimer : IDisposable
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromHours(5);
    // 리셋 경계보다 이르면 요청이 이전 창에 집계돼 새 창이 안 열린다. 서버가 내려준
    // 리셋 시각의 초 단위 오차와 로컬 시계 어긋남을 함께 덮으려고 30초를 둔다.
    // 늦어서 잃는 건 창 시작이 그만큼 밀리는 것뿐이라 여유를 크게 잡는 편이 낫다.
    private static readonly TimeSpan TriggerDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxLate = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string StateMutexName = "Local\\AiQuotaTray.WindowPrimerState";

    private readonly AppSettings _settings;
    private readonly Timer _timer;
    private int _running;

    private static string StateFile => Path.Combine(AppSettings.SettingsDirectory, "window-primer.json");
    private static string TaskXmlFile(string provider) => Path.Combine(AppSettings.SettingsDirectory, $"window-primer-{provider.ToLowerInvariant()}.xml");
    private static string TaskName(string provider) => $"AI Quota Tray - Prime {provider}";

    public WindowPrimer(AppSettings settings)
    {
        _settings = settings;
        SettingsChanged();
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
    }

    public void Observe(IReadOnlyList<ProviderUsage> usages)
    {
        if (!_settings.KeepFiveHourWindowsAligned) return;
        var schedules = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        WithState(state =>
        {
            foreach (var usage in usages)
            {
                var reset = usage.Windows.Where(w => w.Kind == WindowKind.Session && w.ResetsAt is not null)
                    .Select(w => w.ResetsAt!.Value).OrderBy(v => v).FirstOrDefault();
                if (reset == default || !ProviderEnabled(_settings, usage.Provider)) continue;
                if (!state.TryGetValue(usage.Provider, out var current) || reset > current) state[usage.Provider] = reset;
                schedules[usage.Provider] = state[usage.Provider];
            }
        });
        foreach (var pair in schedules) RegisterWakeTask(pair.Key, pair.Value);
    }

    public void SettingsChanged()
    {
        if (!_settings.KeepFiveHourWindowsAligned)
        {
            WithState(state => state.Clear());
            DeleteWakeTask("Claude");
            DeleteWakeTask("Codex");
            return;
        }

        var schedules = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        WithState(state =>
        {
            foreach (string provider in state.Keys.ToArray())
            {
                if (!ProviderEnabled(_settings, provider)) { state.Remove(provider); continue; }
                state[provider] = NormalizeFuture(state[provider], DateTime.Now);
                schedules[provider] = state[provider];
            }
        });
        foreach (string provider in new[] { "Claude", "Codex" })
        {
            if (schedules.TryGetValue(provider, out var scheduled)) RegisterWakeTask(provider, scheduled);
            else DeleteWakeTask(provider);
        }
    }

    public static async Task RunScheduledAsync(string provider, long scheduledTicks)
    {
        var settings = AppSettings.Load();
        if (!settings.KeepFiveHourWindowsAligned || !ProviderEnabled(settings, provider))
        {
            DeleteWakeTask(provider);
            return;
        }
        await ClaimAndPrimeAsync(settings, provider, new DateTime(scheduledTicks, DateTimeKind.Local)).ConfigureAwait(false);
    }

    private async Task TickAsync()
    {
        if (!_settings.KeepFiveHourWindowsAligned || Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            List<(string Provider, DateTime Scheduled)> due = WithState(state => state
                .Where(p => DateTime.Now >= p.Value + TriggerDelay && ProviderEnabled(_settings, p.Key))
                .Select(p => (p.Key, p.Value)).ToList(), save: false);
            foreach (var item in due) await ClaimAndPrimeAsync(_settings, item.Provider, item.Scheduled).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static async Task ClaimAndPrimeAsync(AppSettings settings, string provider, DateTime scheduled)
    {
        bool shouldPrime = false;
        DateTime next = default;
        WithState(state =>
        {
            if (!state.TryGetValue(provider, out var current) || current.Ticks != scheduled.Ticks) return;
            DateTime now = DateTime.Now;
            shouldPrime = now >= current + TriggerDelay && now - (current + TriggerDelay) <= MaxLate;
            next = AdvanceToFuture(current, now);
            state[provider] = next;
        });
        if (next == default) return;
        RegisterWakeTask(provider, next);
        if (shouldPrime && settings.KeepFiveHourWindowsAligned && ProviderEnabled(settings, provider))
            await PrimeAsync(provider).ConfigureAwait(false);
    }

    private static DateTime AdvanceToFuture(DateTime scheduled, DateTime now)
    {
        do { scheduled += WindowLength; } while (scheduled + TriggerDelay <= now);
        return scheduled;
    }

    private static DateTime NormalizeFuture(DateTime scheduled, DateTime now)
    {
        while (scheduled + TriggerDelay <= now) scheduled += WindowLength;
        return scheduled;
    }

    private static bool ProviderEnabled(AppSettings settings, string provider) => provider switch
    {
        "Claude" => settings.ClaudeEnabled,
        "Codex" => settings.CodexEnabled,
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
                FileName = executable, WorkingDirectory = AppSettings.PrimerWorkingDirectory,
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            string[] args = provider == "Claude"
                ? new[] { "--bg", "--safe-mode", "--no-chrome", "--tools", "", "--effort", "low", "--permission-mode", "plan", "--name", "AI Quota Tray window start", "Reply with OK only. Do not use tools." }
                : new[] { "exec", "--ephemeral", "--ignore-user-config", "--skip-git-repo-check", "--sandbox", "read-only", "-c", "model_reasoning_effort=\"low\"", "Reply with OK only. Do not use tools." };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return;
            Task stdout = process.StandardOutput.ReadToEndAsync();
            Task stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch { }
    }

    private static void RegisterWakeTask(string provider, DateTime scheduled)
    {
        try
        {
            string? app = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(app) || !File.Exists(app)) return;
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            string userSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
            string startBoundary = (scheduled + TriggerDelay).ToString("yyyy-MM-dd'T'HH:mm:ss");
            string arguments = $"--prime-window {provider} {scheduled.Ticks}";
            string xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo><Description>Wakes the computer to keep the {Escape(provider)} 5-hour quota window aligned.</Description></RegistrationInfo>
                  <Triggers><TimeTrigger><StartBoundary>{startBoundary}</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
                  <Principals><Principal id="Author"><UserId>{Escape(userSid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
                  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>false</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>true</WakeToRun><ExecutionTimeLimit>PT5M</ExecutionTimeLimit><Priority>7</Priority></Settings>
                  <Actions Context="Author"><Exec><Command>{Escape(app)}</Command><Arguments>{Escape(arguments)}</Arguments><WorkingDirectory>{Escape(AppSettings.PrimerWorkingDirectory)}</WorkingDirectory></Exec></Actions>
                </Task>
                """;
            string xmlFile = TaskXmlFile(provider);
            File.WriteAllText(xmlFile, xml, Encoding.Unicode);
            RunSchtasks("/Create", "/TN", TaskName(provider), "/XML", xmlFile, "/F");
        }
        catch { }
    }

    private static void DeleteWakeTask(string provider) { try { RunSchtasks("/Delete", "/TN", TaskName(provider), "/F"); } catch { } }

    private static void RunSchtasks(params string[] args)
    {
        var start = new ProcessStartInfo { FileName = "schtasks.exe", UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        process?.WaitForExit(10_000);
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? "";

    private static string? FindExecutable(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { string candidate = Path.Combine(dir.Trim(), name); if (File.Exists(candidate)) return candidate; }
            catch { }
        }
        return null;
    }

    private static void WithState(Action<Dictionary<string, DateTime>> action) => WithState(state => { action(state); return true; });

    private static T WithState<T>(Func<Dictionary<string, DateTime>, T> action, bool save = true)
    {
        using var mutex = new Mutex(false, StateMutexName);
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return default!;
            Dictionary<string, DateTime> state = LoadState();
            T result = action(state);
            if (save) SaveState(state);
            return result;
        }
        catch { return default!; }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static Dictionary<string, DateTime> LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return new(StringComparer.OrdinalIgnoreCase);
            var state = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(StateFile));
            return state is null ? new(StringComparer.OrdinalIgnoreCase) : new(state, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void SaveState(Dictionary<string, DateTime> state)
    {
        Directory.CreateDirectory(AppSettings.SettingsDirectory);
        string tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tmp, StateFile, overwrite: true);
    }

    public void Dispose() => _timer.Dispose();
}
