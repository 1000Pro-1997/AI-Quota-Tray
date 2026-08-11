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

/// <summary>선택한 공급자의 5시간/주간 창이 초기화된 직후 최소 요청을 한 번 보낸다.</summary>
public sealed class WindowPrimer : IDisposable
{
    // 리셋 경계보다 이르면 요청이 이전 창에 집계돼 새 창이 안 열린다. 서버가 내려준
    // 리셋 시각의 초 단위 오차와 로컬 시계 어긋남을 함께 덮으려고 30초를 둔다.
    // 늦어서 잃는 건 창 시작이 그만큼 밀리는 것뿐이라 여유를 크게 잡는 편이 낫다.
    private static readonly TimeSpan TriggerDelay = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string StateMutexName = "Local\\AiQuotaTray.WindowPrimerState";

    private readonly AppSettings _settings;
    private readonly Timer _timer;
    private int _running;
    private IReadOnlyList<ProviderUsage> _observed = Array.Empty<ProviderUsage>();

    public event Action? PredictedResetApplied;
    public event Action<string, WindowKind, DateTime>? WindowStarted;

    private static string StateFile => Path.Combine(AppSettings.SettingsDirectory, "window-primer.json");
    private static string PredictionsFile => Path.Combine(AppSettings.SettingsDirectory, "window-primer-predictions.json");
    private static string ScheduleKey(string provider, WindowKind kind) => $"{provider}:{kind}";
    private static string TaskXmlFile(string provider, WindowKind kind) => Path.Combine(AppSettings.SettingsDirectory, $"window-primer-{provider.ToLowerInvariant()}-{kind.ToString().ToLowerInvariant()}.xml");
    private static string TaskName(string provider, WindowKind kind) => $"AI Quota Tray - Prime {provider} {kind}";

    public WindowPrimer(AppSettings settings)
    {
        _settings = settings;
        SettingsChanged();
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
    }

    public void Observe(IReadOnlyList<ProviderUsage> usages)
    {
        _observed = usages;
        ApplyPredictedResets(usages);
        if (!AnyPrimerEnabled(_settings)) return;
        var schedules = new Dictionary<string, (string Provider, WindowKind Kind, DateTime At)>(StringComparer.OrdinalIgnoreCase);
        WithState(state =>
        {
            foreach (var usage in usages)
            {
                foreach (var window in usage.Windows.Where(w =>
                             (w.Kind == WindowKind.Session || w.Kind == WindowKind.Weekly) &&
                             w.ResetsAt is not null && PrimerEnabled(_settings, usage.Provider, w.Kind)))
                {
                    string key = ScheduleKey(usage.Provider, window.Kind);
                    DateTime reset = window.ResetsAt!.Value;
                    if (!state.TryGetValue(key, out var current) || reset > current) state[key] = reset;
                    schedules[key] = (usage.Provider, window.Kind, state[key]);
                }
            }
        });
        foreach (var schedule in schedules.Values) RegisterWakeTask(schedule.Provider, schedule.Kind, schedule.At);
    }

    public void SettingsChanged()
    {
        if (!AnyPrimerEnabled(_settings))
        {
            WithState(state => state.Clear());
            DeleteAllWakeTasks();
            return;
        }

        var schedules = new List<(string Provider, WindowKind Kind, DateTime At)>();
        WithState(state =>
        {
            foreach (string key in state.Keys.ToArray())
            {
                if (!TryParseScheduleKey(key, out string provider, out WindowKind kind) ||
                    !PrimerEnabled(_settings, provider, kind)) { state.Remove(key); continue; }
                // 지난 시각을 여기서 미래로 넘기면 부팅 중 놓친 메시지를 영영 보내지
                // 못한다. TickAsync가 성공 여부를 확인한 뒤 다음 시각으로 넘긴다.
                schedules.Add((provider, kind, state[key]));
            }
        });
        foreach (string provider in new[] { "Claude", "Codex" })
        {
            foreach (WindowKind kind in new[] { WindowKind.Session, WindowKind.Weekly })
            {
                var schedule = schedules.FirstOrDefault(s => s.Provider == provider && s.Kind == kind);
                if (schedule.At != default) RegisterWakeTask(provider, kind, schedule.At);
                else DeleteWakeTask(provider, kind);
            }
        }
        DeleteLegacyWakeTasks();
    }

    public static async Task RunScheduledAsync(string provider, WindowKind kind, long scheduledTicks)
    {
        var settings = AppSettings.Load();
        if (!PrimerEnabled(settings, provider, kind))
        {
            DeleteWakeTask(provider, kind);
            return;
        }
        await ClaimAndPrimeAsync(settings, provider, kind,
            new DateTime(scheduledTicks, DateTimeKind.Local)).ConfigureAwait(false);
    }

    private async Task TickAsync()
    {
        if (!AnyPrimerEnabled(_settings) || Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            List<(string Provider, WindowKind Kind, DateTime Scheduled)> due = WithState(state => state
                .Where(p => DateTime.Now >= p.Value + TriggerDelay &&
                            TryParseScheduleKey(p.Key, out string provider, out WindowKind kind) &&
                            PrimerEnabled(_settings, provider, kind))
                .Select(p => { TryParseScheduleKey(p.Key, out string provider, out WindowKind kind); return (provider, kind, p.Value); }).ToList(), save: false);
            foreach (var item in due)
                await ClaimAndPrimeAsync(_settings, item.Provider, item.Kind, item.Scheduled,
                    started => WindowStarted?.Invoke(item.Provider, item.Kind, started)).ConfigureAwait(false);
            if (ApplyPredictedResets(_observed)) PredictedResetApplied?.Invoke();
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static async Task ClaimAndPrimeAsync(AppSettings settings, string provider,
        WindowKind kind, DateTime scheduled, Action<DateTime>? windowStarted = null)
    {
        bool shouldPrime = false;
        DateTime next = default;
        WithState(state =>
        {
            string key = ScheduleKey(provider, kind);
            if (!state.TryGetValue(key, out var current) || current.Ticks != scheduled.Ticks) return;
            DateTime now = DateTime.Now;
            // PC가 꺼져 있었더라도 다음 실행에서 놓친 구간을 한 번은 시작한다.
            shouldPrime = now >= current + TriggerDelay;
            next = AdvanceToFuture(current, now, WindowLength(kind));
            state[key] = next;
        });
        if (next == default) return;
        RegisterWakeTask(provider, kind, next);
        if (shouldPrime && PrimerEnabled(settings, provider, kind))
        {
            DateTime started = DateTime.Now;
            if (await PrimeAsync(provider).ConfigureAwait(false))
            {
                SavePrediction(provider, kind, started + WindowLength(kind));
                if (windowStarted is null)
                    UsageCache.ApplyWindowStarted(provider, kind, started);
                else
                    windowStarted(started);
            }
        }
    }

    private static DateTime AdvanceToFuture(DateTime scheduled, DateTime now, TimeSpan length)
    {
        do { scheduled += length; } while (scheduled + TriggerDelay <= now);
        return scheduled;
    }

    private static TimeSpan WindowLength(WindowKind kind) =>
        kind == WindowKind.Weekly ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5);

    private static bool AnyPrimerEnabled(AppSettings settings) =>
        settings.ClaudePrimeFiveHour || settings.ClaudePrimeWeekly ||
        settings.CodexPrimeFiveHour || settings.CodexPrimeWeekly;

    private static bool PrimerEnabled(AppSettings settings, string provider, WindowKind kind) => (provider, kind) switch
    {
        ("Claude", WindowKind.Session) => settings.ClaudeEnabled && settings.ClaudePrimeFiveHour,
        ("Claude", WindowKind.Weekly) => settings.ClaudeEnabled && settings.ClaudePrimeWeekly,
        ("Codex", WindowKind.Session) => settings.CodexEnabled && settings.CodexPrimeFiveHour,
        ("Codex", WindowKind.Weekly) => settings.CodexEnabled && settings.CodexPrimeWeekly,
        _ => false,
    };

    private static bool TryParseScheduleKey(string key, out string provider, out WindowKind kind)
    {
        int separator = key.LastIndexOf(':');
        provider = separator > 0 ? key[..separator] : "";
        kind = WindowKind.Other;
        return separator > 0 && Enum.TryParse(key[(separator + 1)..], true, out kind) &&
               (kind == WindowKind.Session || kind == WindowKind.Weekly);
    }

    private static async Task<bool> PrimeAsync(string provider)
    {
        try
        {
            string? executable = FindExecutable(provider == "Claude" ? "claude.exe" : "codex.exe");
            if (executable is null) return false;
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
            if (process is null) return false;
            Task stdout = process.StandardOutput.ReadToEndAsync();
            Task stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void SavePrediction(string provider, WindowKind kind, DateTime reset)
    {
        WithState(_ =>
        {
            var predictions = LoadDictionary(PredictionsFile);
            predictions[ScheduleKey(provider, kind)] = reset;
            SaveDictionary(PredictionsFile, predictions);
        });
    }

    private static bool ApplyPredictedResets(IReadOnlyList<ProviderUsage> usages)
    {
        bool changed = false;
        WithState(_ =>
        {
            var predictions = LoadDictionary(PredictionsFile);
            foreach (var usage in usages)
            foreach (var window in usage.Windows)
            {
                string key = ScheduleKey(usage.Provider, window.Kind);
                if (!predictions.TryGetValue(key, out DateTime predicted)) continue;

                if (predicted <= DateTime.Now)
                {
                    predictions.Remove(key);
                    continue;
                }

                // 서버가 미래의 실제 시각을 주면 계산값은 더 이상 필요 없다.
                if (window.ResetsAt is { } actual && actual > DateTime.Now &&
                    Math.Abs((actual - predicted).TotalSeconds) > 1)
                {
                    predictions.Remove(key);
                    continue;
                }

                window.ResetsAt = predicted;
                changed = true;
            }
            SaveDictionary(PredictionsFile, predictions);
        });
        return changed;
    }

    private static void RegisterWakeTask(string provider, WindowKind kind, DateTime scheduled)
    {
        try
        {
            string? app = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(app) || !File.Exists(app)) return;
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            string userSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
            string startBoundary = (scheduled + TriggerDelay).ToString("yyyy-MM-dd'T'HH:mm:ss");
            string arguments = $"--prime-window {provider} {kind} {scheduled.Ticks}";
            string xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo><Description>Wakes the computer to start the {Escape(provider)} {kind} quota window after reset.</Description></RegistrationInfo>
                  <Triggers><TimeTrigger><StartBoundary>{startBoundary}</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
                  <Principals><Principal id="Author"><UserId>{Escape(userSid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
                  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>false</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>true</WakeToRun><ExecutionTimeLimit>PT5M</ExecutionTimeLimit><Priority>7</Priority></Settings>
                  <Actions Context="Author"><Exec><Command>{Escape(app)}</Command><Arguments>{Escape(arguments)}</Arguments><WorkingDirectory>{Escape(AppSettings.PrimerWorkingDirectory)}</WorkingDirectory></Exec></Actions>
                </Task>
                """;
            string xmlFile = TaskXmlFile(provider, kind);
            File.WriteAllText(xmlFile, xml, Encoding.Unicode);
            RunSchtasks("/Create", "/TN", TaskName(provider, kind), "/XML", xmlFile, "/F");
        }
        catch { }
    }

    private static void DeleteWakeTask(string provider, WindowKind kind) { try { RunSchtasks("/Delete", "/TN", TaskName(provider, kind), "/F"); } catch { } }

    private static void DeleteAllWakeTasks()
    {
        foreach (string provider in new[] { "Claude", "Codex" })
            foreach (WindowKind kind in new[] { WindowKind.Session, WindowKind.Weekly })
                DeleteWakeTask(provider, kind);
        DeleteLegacyWakeTasks();
    }

    private static void DeleteLegacyWakeTasks()
    {
        foreach (string provider in new[] { "Claude", "Codex" })
            try { RunSchtasks("/Delete", "/TN", $"AI Quota Tray - Prime {provider}", "/F"); } catch { }
    }

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
        => LoadDictionary(StateFile);

    private static Dictionary<string, DateTime> LoadDictionary(string file)
    {
        try
        {
            if (!File.Exists(file)) return new(StringComparer.OrdinalIgnoreCase);
            var state = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(file));
            return state is null ? new(StringComparer.OrdinalIgnoreCase) : new(state, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void SaveState(Dictionary<string, DateTime> state)
        => SaveDictionary(StateFile, state);

    private static void SaveDictionary(string file, Dictionary<string, DateTime> state)
    {
        Directory.CreateDirectory(AppSettings.SettingsDirectory);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tmp, file, overwrite: true);
    }

    public void Dispose() => _timer.Dispose();
}
