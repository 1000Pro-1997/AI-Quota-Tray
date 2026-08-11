using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Models;

namespace AiUsageTray.Services;

/// <summary>모든 공급자를 주기적으로 조회하고 결과를 알린다.</summary>
public sealed class UsageMonitor : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _inflight;

    /// <summary>마지막으로 성공한 값. 조회가 실패하면 이걸 대신 보여준다.</summary>
    private readonly Dictionary<string, ProviderUsage> _lastGood = new();

    /// <summary>마지막 조회 시각. 이보다 최근이면 다시 부르지 않는다.</summary>
    private DateTime _lastFetch = DateTime.MinValue;

    /// <summary>
    /// 마지막으로 새로고침을 실제로 시도한 시각. 조회가 실패해도 갱신된다.
    /// 화면의 "몇 분 전 새로고침"은 수치의 나이가 아니라 이 시각을 쓴다.
    /// 사용자가 버튼을 눌렀으면 눌렀다고 보여주는 게 맞다.
    /// 아직 한 번도 돌지 않았으면 null.
    /// </summary>
    public DateTime? LastRefreshAt { get; private set; }

    /// <summary>
    /// 이 시간 안에 다시 요청하면 캐시를 쓴다. 팝업을 연달아 열어도
    /// 서버를 두드리지 않게 해 429를 막는다.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    /// <summary>서비스 장애 상태. 사용량과 별개로 조회한다.</summary>
    public StatusProvider Status { get; }

    private ClaudeProvider? _claude;
    private CodexProvider? _codex;

    /// <summary>새 결과가 준비되면 발생. UI 스레드가 아닌 곳에서 호출될 수 있다.</summary>
    public event Action<IReadOnlyList<ProviderUsage>>? Updated;

    /// <summary>조회 시작/종료를 알린다. 스피너 표시용.</summary>
    public event Action<bool>? BusyChanged;

    public IReadOnlyList<ProviderUsage> Latest { get; private set; } = Array.Empty<ProviderUsage>();

    public UsageMonitor(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AiUsageTray/1.0");

        Status = new StatusProvider(_http);

        // 지난 실행에서 남긴 값을 불러온다. 첫 조회가 실패해도 보여줄 것이 생긴다.
        foreach (var kv in UsageCache.Load())
            _lastGood[kv.Key] = kv.Value;

        EnsureLocalBaseline("Claude", _settings.ClaudeEnabled,
            WindowKind.Session, WindowKind.Weekly);
        EnsureLocalBaseline("Codex", _settings.CodexEnabled, WindowKind.Weekly);

        // 불러온 값을 곧바로 화면용으로도 쓴다. Claude는 조회에 네트워크 왕복이
        // 필요해 첫 결과까지 수 초가 걸리는데, 그동안 위젯바와 트레이가 비어 있으면
        // 고장으로 보인다. _lastFetch는 MinValue 그대로 두어 시작 직후의 실제
        // 조회가 캐시에 막히지 않게 한다.
        Latest = _lastGood.Values
            .Where(u => u.IsAvailable && u.Windows.Count > 0)
            .ToList();
    }

    /// <summary>
    /// 설치 직후이거나 구형 캐시가 깨졌어도 공급자 카드 자체는 반드시 보여준다.
    /// 서버에서 처음 성공하면 이 0% 기준값은 즉시 실제 값으로 교체된다.
    /// </summary>
    private void EnsureLocalBaseline(string provider, bool enabled, params WindowKind[] kinds)
    {
        if (!enabled || _lastGood.TryGetValue(provider, out var existing) &&
            existing.Windows.Count > 0) return;

        _lastGood[provider] = new ProviderUsage
        {
            Provider = provider,
            Windows = kinds.Select(kind => new UsageWindow
            {
                Kind = kind,
                Percent = 0,
            }).ToList(),
            LastUpdated = DateTime.Now,
        };
        UsageCache.Save(_lastGood.Values);
    }

    /// <summary>
    /// 사용량을 갱신한다. 방금 가져온 값이 있으면 서버를 다시 부르지 않는다.
    /// </summary>
    /// <param name="force">캐시를 무시하고 반드시 조회한다. 새로고침 버튼용.</param>
    public async Task RefreshAsync(bool force = false)
    {
        // 최근에 가져온 값이 있으면 그대로 쓴다. 팝업을 여러 번 열어도 호출은 한 번.
        if (!force && Latest.Count > 0 && DateTime.Now - _lastFetch < MinInterval)
        {
            Updated?.Invoke(Latest);
            return;
        }

        var cts = new CancellationTokenSource();

        // 앞선 요청이 남아 있으면 취소한다. 이미 정리된 것이면 무시한다.
        var stale = Interlocked.Exchange(ref _inflight, cts);
        try
        {
            stale?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 이미 끝나고 정리된 요청이다. 취소할 것이 없다.
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (cts.IsCancellationRequested) return;

            // 대기하는 사이 다른 호출이 값을 채웠을 수 있다.
            if (!force && Latest.Count > 0 && DateTime.Now - _lastFetch < MinInterval)
            {
                Updated?.Invoke(Latest);
                return;
            }

            BusyChanged?.Invoke(true);

            var providers = BuildProviders();

            // 상태 조회는 곁다리다. 실패해도 사용량 조회를 막지 않는다.
            var statusTask = SafeStatus(cts.Token);

            // 이번 회차에서 조회할 대상만 담는다. 아직 안 끝난 것은 비어 있고,
            // 그 자리는 화면을 만들 때 직전 값으로 메운다.
            var fresh = new Dictionary<string, ProviderUsage>();

            // 공급자마다 걸리는 시간이 크게 다르다. Codex는 로컬 파일이라 즉시
            // 끝나는데 Claude는 네트워크 왕복이 필요하다. 함께 기다리면 빠른 쪽이
            // 느린 쪽에 묶여 몇 초씩 늦어지므로, 끝나는 대로 각각 화면에 올린다.
            var running = providers.Select(async p =>
            {
                var u = await SafeFetch(p, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) return u;

                lock (fresh)
                {
                    if (u.Error is null) _lastGood[u.Provider] = u;
                    fresh[u.Provider] = u;

                    // 아직 안 온 공급자는 직전 값으로 채워 자리를 지킨다.
                    // 그러지 않으면 먼저 온 쪽을 그리는 순간 나머지가 사라진다.
                    Publish(providers, fresh);
                }

                return u;
            }).ToList();

            var fetched = await Task.WhenAll(running).ConfigureAwait(false);
            await statusTask.ConfigureAwait(false);

            if (cts.IsCancellationRequested) return;

            // 전부 실패했다면 캐시 시각을 갱신하지 않는다.
            // 그래야 잠시 뒤 다시 시도할 수 있다. 성공값 하나라도 있으면 캐시한다.
            if (fetched.Any(u => u.Error is null))
            {
                _lastFetch = DateTime.Now;
                UsageCache.Save(_lastGood.Values);
            }

            // 새로고침 시각은 결과와 무관하게 남긴다. 실패했더라도
            // 사용자가 보기엔 방금 새로고침한 것이 맞다.
            LastRefreshAt = DateTime.Now;

            // 마지막으로 한 번 더 알린다. 위의 중간 알림은 LastRefreshAt이
            // 갱신되기 전에 나갔으므로, "방금 새로고침됨" 표시가 여기서 맞춰진다.
            lock (fresh)
            {
                Publish(providers, fresh);
            }
        }
        finally
        {
            BusyChanged?.Invoke(false);
            _gate.Release();

            // 내가 등록한 것이 아직 걸려 있을 때만 떼어낸 뒤 정리한다.
            Interlocked.CompareExchange(ref _inflight, null, cts);
            cts.Dispose();
        }
    }

    /// <summary>
    /// 지금까지 받은 결과로 화면용 목록을 만들어 알린다.
    ///
    /// 아직 응답이 오지 않은 공급자는 직전 성공값으로 자리를 지킨다. 그래야
    /// 먼저 도착한 쪽을 그리는 동안 나머지가 화면에서 사라지지 않는다.
    /// 조회 순서와 무관하게 늘 같은 자리에 오도록 공급자 순서를 따른다.
    ///
    /// 호출자가 fresh를 잠근 상태에서 부른다.
    /// </summary>
    private void Publish(List<IUsageProvider> providers, Dictionary<string, ProviderUsage> fresh)
    {
        var shown = new List<ProviderUsage>(providers.Count);

        foreach (var p in providers)
        {
            if (fresh.TryGetValue(p.Name, out var u))
            {
                if (u.Error is null) shown.Add(u);

                // 갱신은 못 했지만 직전 수치는 보여줄 수 있다.
                else if (_lastGood.TryGetValue(p.Name, out var previous))
                    shown.Add(previous.AsStale(u.Error));

                else shown.Add(u);
            }
            else if (_lastGood.TryGetValue(p.Name, out var previous))
            {
                // 아직 조회 중이다. 직전 값을 그대로 두어 깜빡임을 막는다.
                shown.Add(previous);
            }
        }

        Latest = shown;
        Updated?.Invoke(shown);
    }

    /// <summary>상태 조회 실패가 사용량 갱신을 막지 않게 한다.</summary>
    private async Task SafeStatus(CancellationToken ct)
    {
        try
        {
            await Status.RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // 상태를 못 가져와도 사용량은 계속 보여준다.
        }
    }

    /// <summary>한 공급자가 실패해도 나머지 결과는 살린다.</summary>
    private static async Task<ProviderUsage> SafeFetch(IUsageProvider p, CancellationToken ct)
    {
        try
        {
            return await p.FetchAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ProviderUsage.Unavailable(p.Name, Strings.Get("error.cancelled"));
        }
        catch (Exception ex)
        {
            return ProviderUsage.Unavailable(p.Name, ex.Message);
        }
    }

    /// <summary>
    /// 설정이 바뀌면 화면을 곧바로 맞춘다.
    ///
    /// 꺼진 도구는 화면에서 빼고, 켜진 도구는 마지막으로 성공한 값을 되살린다.
    /// 캐시는 지우지 않는다. 지워버리면 다시 켰을 때 서버가 막혀 있는 동안
    /// (429 등) 보여줄 것이 없어진다.
    /// </summary>
    public void ApplyEnabledChange()
    {
        var shown = new List<ProviderUsage>();

        foreach (string name in new[] { "Claude", "Codex" })
        {
            bool enabled = name switch
            {
                "Claude" => _settings.ClaudeEnabled,
                "Codex" => _settings.CodexEnabled,
                _ => true,
            };

            if (!enabled) continue;

            // 이미 보이던 값이 있으면 그대로 쓰고, 없으면 캐시에서 되살린다.
            var current = Latest.FirstOrDefault(u => u.Provider == name);
            if (current is not null) shown.Add(current);
            else if (_lastGood.TryGetValue(name, out var cached)) shown.Add(cached);
        }

        Latest = shown;
        Updated?.Invoke(Latest);
    }

    /// <summary>성공한 자동 메시지로 바뀐 로컬 구간을 화면과 디스크에 함께 반영한다.</summary>
    public void ApplyLocalWindowStart(string provider, WindowKind kind, DateTime startedAt)
    {
        var cached = UsageCache.ApplyWindowStarted(provider, kind, startedAt);
        if (!cached.TryGetValue(provider, out var updated)) return;

        _lastGood[provider] = updated;
        var shown = Latest.Select(u => u.Provider == provider ? updated : u).ToList();
        if (shown.All(u => u.Provider != provider)) shown.Add(updated);
        Latest = shown;
        Updated?.Invoke(Latest);
    }

    /// <summary>
    /// 프로바이더는 한 번만 만들어 재사용한다. 매번 새로 만들면
    /// 429 백오프 같은 내부 상태가 사라져 제한을 계속 두드리게 된다.
    /// 활성화 여부만 매 호출 시점의 설정을 따른다.
    /// </summary>
    private List<IUsageProvider> BuildProviders()
    {
        _claude ??= new ClaudeProvider(_http, () => _settings.EffectiveClaudePath);
        _codex ??= new CodexProvider(() => _settings.EffectiveCodexPath);

        var list = new List<IUsageProvider>();
        if (_settings.ClaudeEnabled) list.Add(_claude);
        if (_settings.CodexEnabled) list.Add(_codex);
        return list;
    }

    public void Dispose()
    {
        _inflight?.Cancel();
        _inflight?.Dispose();
        _http.Dispose();
        _gate.Dispose();
    }
}
