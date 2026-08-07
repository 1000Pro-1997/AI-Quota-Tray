using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageTray.Services;

/// <summary>업데이트 확인 결과.</summary>
public sealed class UpdateInfo
{
    /// <summary>릴리스에 붙은 버전. 예: 1.2.0</summary>
    public Version? Latest { get; init; }

    /// <summary>사람이 읽는 태그. 예: v1.2.0</summary>
    public string TagName { get; init; } = "";

    /// <summary>릴리스 페이지 주소.</summary>
    public string PageUrl { get; init; } = "";

    /// <summary>지금 버전보다 새 것이 있는가.</summary>
    public bool HasUpdate { get; init; }

    /// <summary>확인에 실패한 사유. 성공이면 null.</summary>
    public string? Error { get; init; }

    public static UpdateInfo Failed(string reason) => new() { Error = reason };
}

/// <summary>
/// GitHub Releases에서 새 버전이 있는지 확인한다.
///
/// 공개 저장소라 인증이 필요 없다. 다운로드는 하지 않고,
/// 사용자가 누르면 브라우저로 릴리스 페이지를 연다.
/// </summary>
public sealed class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/1000Pro-1997/AI-Quota-Tray/releases/latest";

    /// <summary>이 간격 안에는 다시 묻지 않는다. GitHub API는 시간당 60회 제한이 있다.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly AppSettings _settings;

    public UpdateChecker(HttpClient http, AppSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>마지막으로 확인한 결과. 아직 확인 전이면 null.</summary>
    public UpdateInfo? Last { get; private set; }

    /// <summary>지금 실행 중인 버전.</summary>
    public static Version Current
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? new Version(1, 0, 0) : new Version(v.Major, v.Minor, v.Build);
        }
    }

    /// <summary>하루가 지났으면 확인한다. 시작할 때 부른다.</summary>
    public async Task CheckIfDueAsync(CancellationToken ct = default)
    {
        if (DateTime.Now - _settings.LastUpdateCheck < CheckInterval) return;

        await CheckAsync(ct).ConfigureAwait(false);
    }

    /// <summary>지금 바로 확인한다. 설정의 버튼에서 부른다.</summary>
    public async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);

            // GitHub API는 User-Agent를 요구한다.
            req.Headers.TryAddWithoutValidation("User-Agent", "AiQuotaTray");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);

            // 릴리스를 아직 하나도 올리지 않았으면 404가 온다. 오류가 아니다.
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _settings.LastUpdateCheck = DateTime.Now;
                _settings.Save();

                return Last = new UpdateInfo { HasUpdate = false };
            }

            if (!res.IsSuccessStatusCode)
                return Last = UpdateInfo.Failed(Strings.Get("update.failed"));

            string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var info = Parse(body);

            // 확인에 성공했을 때만 시각을 남긴다. 실패는 다시 시도할 수 있게 둔다.
            if (info.Error is null)
            {
                _settings.LastUpdateCheck = DateTime.Now;
                _settings.Save();
            }

            return Last = info;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            return Last = UpdateInfo.Failed(Strings.Get("error.network"));
        }
        catch (Exception ex)
        {
            return Last = UpdateInfo.Failed(ex.Message);
        }
    }

    private static UpdateInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 초안이나 사전 배포판은 건너뛴다.
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            return new UpdateInfo { HasUpdate = false };

        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
            return new UpdateInfo { HasUpdate = false };

        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";

        var latest = ParseVersion(tag);
        if (latest is null)
            return new UpdateInfo { TagName = tag, PageUrl = page, HasUpdate = false };

        return new UpdateInfo
        {
            Latest = latest,
            TagName = tag,
            PageUrl = string.IsNullOrEmpty(page) ? AppInfo.Repository + "/releases" : page,
            HasUpdate = latest > Current,
        };
    }

    /// <summary>"v1.2.0", "1.2", "1.2.0-beta" 같은 태그에서 숫자만 뽑는다.</summary>
    private static Version? ParseVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        string s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];

        // 뒤에 붙은 -beta 같은 꼬리표를 떼어낸다.
        int dash = s.IndexOf('-');
        if (dash > 0) s = s[..dash];

        var parts = s.Split('.');
        if (parts.Length == 0) return null;

        int[] numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (i >= parts.Length) { numbers[i] = 0; continue; }
            if (!int.TryParse(parts[i], out numbers[i])) return null;
        }

        return new Version(numbers[0], numbers[1], numbers[2]);
    }
}
