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

            var fetched = await Task.WhenAll(
                providers.Select(p => SafeFetch(p, cts.Token))).ConfigureAwait(false);

            await statusTask.ConfigureAwait(false);

            if (cts.IsCancellationRequested) return;

            var results = new ProviderUsage[fetched.Length];
            for (int i = 0; i < fetched.Length; i++)
            {
                var u = fetched[i];

                if (u.Error is null)
                {
                    _lastGood[u.Provider] = u;
                    results[i] = u;
                }
                else if (_lastGood.TryGetValue(u.Provider, out var previous))
                {
                    // 갱신은 못 했지만 직전 수치는 보여줄 수 있다.
                    results[i] = previous.AsStale(u.Error);
                }
                else
                {
                    results[i] = u;
                }
            }

            // 전부 실패했다면 캐시 시각을 갱신하지 않는다.
            // 그래야 잠시 뒤 다시 시도할 수 있다. 성공값 하나라도 있으면 캐시한다.
            if (fetched.Any(u => u.Error is null))
            {
                _lastFetch = DateTime.Now;
                UsageCache.Save(_lastGood.Values);
            }

            Latest = results;
            Updated?.Invoke(results);
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
